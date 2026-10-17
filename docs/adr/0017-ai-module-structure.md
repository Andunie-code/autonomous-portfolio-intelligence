# ADR-0017: AI modülünün yapısı, Gemini adapter'ı ve Researcher sınırı

- Durum: **Kabul edildi (Kapı A, kullanıcı onayı 2026-10-18)** · Tarih: 2026-10-18
- Karar türü: **T** (teknik lider). Kullanıcı kararları ayrıca belirtildi.
- Uygulama durumu: **Kapı B'de uygulandı (2026-10-18), çevrimdışı testlerle.** Gerçek Gemini çağrısı yapılmadı; SDK'nın gerçek API'deki davranışı Kapı C spike'ında doğrulanır.
- Uygulama notları (Kapı B):
  - `ClientOptions.HttpClientFactory` ile enjeksiyon ve SDK retry'ının kapatılması (`HttpRetryOptions.Attempts = 1`) çevrimdışı doğrulandı: 429 ve 503 yanıtlarında tek HTTP isteği gidiyor.
  - Batch sözleşmesi uygulandı (`CreateAsync`, `GetAsync`, `CancelAsync`).
  - Host'lara henüz bağlanmadı (YAGNI). Researcher, M4'te BacktestCli `research-m4` üzerinden çalışır; AiCli ayrı bir araçtır.
  - Kill switch kuralı DB'de de var: otomatik kapatma `CHECK` ile reddedilir.
  - Router'da `NoDecision` bir `TaskOutcome` değeri değildir; neden `DecisionTrace.Reason`'a "no decision: …" olarak yazılır, sonuç `onUnavailable`'dır.
- İlgili: ADR-0007, ADR-0010, ADR-0013, ADR-0016; `architecture.md` §3–§4; `testing-strategy.md` §6; `m4-protocol.md`; `sources.md` S-G1…S-G7 (2026-10-18)

## Bağlam
M4; `IAiProvider`, `ModelRouter`, harcama kontrolü, `AiKillSwitch`, `DecisionTrace`, `EvidenceValidator`, yeterlilik harness'ı ve Researcher tool'larını ekler. Kodda AI bileşeni yoktur. Mevcut kod yalnızca `Gemini:ApiKey` değerini redaction ile maskeler. İki gereksinim belirleyicidir:
- Kapı A ve B'de gerçek API çağrısı yapılmaz; tüm testler sahte sağlayıcıyla ve ağsız koşar.
- Researcher'ın holdout'a, evaluator'a, SQL'e, shell'e ve web'e erişemediği bir sözleşme testiyle gösterilmelidir (roadmap M4).

## Karar

### 1. Projeler
| Proje | Bağımlılıklar | İçerik |
|---|---|---|
| `PortfolioIntelligence.Ai` (`src/Modules/Ai`) | Kernel, Npgsql | Sözleşmeler (`IAiProvider`, `AiRequest`/`AiResponse`, `NeutralSchema`, `NeutralToolSet`, `UsdAmount`, `TaskClass`, `TaskPolicy`), `ModelRouter`, `PriceTable`, `CostLedger`, tavanlar, retry, circuit breaker, `AiKillSwitch`, `DecisionTrace`, `EvidenceValidator`, `QualificationProtocol`, `TaskQualification`, mutabakat raporu, Npgsql store'ları |
| `PortfolioIntelligence.Ai.Gemini` (`src/Modules/Ai.Gemini`) | Ai, `Google.GenAI` | `GeminiProvider` (tek adapter). SDK paketi yalnızca bu projededir |
| `PortfolioIntelligence.Researcher` (`src/Modules/Researcher`) | Kernel, Ai | Agent döngüsü, altı tool'un sözleşmesi, `IResearchToolBackend` portu, hipotez kaynakları (`ApprovedHypothesisSource`, `GridExpansion`, `LlmHypothesisSource`) |
| `PortfolioIntelligence.Research` (mevcut) | + Researcher | Backend portunun uygulaması: mevcut `ResearchGateEvaluator`, `TrialRegistry`, `DatasetCatalog`, `ExperimentRequestGuard`, `ExperimentStore` |
| `PortfolioIntelligence.AiCli` (`src/Tools/AiCli`) | Ai, Ai.Gemini, Infrastructure | `killswitch`, `ledger`, `reconcile`, `spike`, `qualify` |

**Mimari testlerle korunan kurallar:**
- Ai, Execution'a ve Research'e bağlı değildir.
- `Google.GenAI` yalnızca Ai.Gemini'de bulunur.
- Researcher'ın geçişli proje bağımlılıkları yalnızca Kernel ve Ai'dır. Researcher doğrudan `Npgsql` ya da SDK paketi almaz.
- Researcher kaynaklarında `Npgsql`, `System.Diagnostics.Process`, `System.Net.Http` ve HoldoutEvaluator kullanılamaz. Bu kaynak taraması, yerleştirilmiş bir ihlalle öz-test edilir.
- Holdout sır taraması yeni projeleri de kapsar.

Bu yapıda Researcher yalnızca tool portunu görür; bağlantı dizesi, SQL ya da süreç başlatma yetkisi yoktur. Researcher, Ai üzerinden dolaylı olarak Npgsql'e ulaşabilir; ancak bağlantıyı yalnızca host oluşturur ve Researcher'a hiçbir store verilmez. Kaynak taraması bu kuralı kapsar. Tool sözleşme testi, tool kümesinin tam olarak altı tool olduğunu ve izin dışı bir tool çağrısının reddedilip güvenlik ihlali olarak kaydedildiğini doğrular.

### 2. GeminiProvider: resmi SDK, enjekte edilen `HttpClient`
- **SDK:** `Google.GenAI`, `ClientOptions.HttpClientFactory` (`Func<HttpClient>`) ile kendi `HttpClient`'ımızı kabul ediyor (S-G6, 2026-10-18). Bu sayede şunlar mümkün:
  - **Host guard'ı:** yalnızca `generativelanguage.googleapis.com` ve HTTPS; redirect reddi; M1'deki `ArchiveGuardHandler` deseni.
  - **Çevrimdışı testler:** sentetik yanıt dönen sahte `HttpMessageHandler` ve testlerin ağa çıkmadığını doğrulayan bir test.
- **İptal:** `GenerateContentAsync`, `CancellationToken` alır.
- **Gerçek model ve token'lar:** yanıttaki `ModelVersion` alanı `actualModel` olarak kaydedilir. `UsageMetadata` alanlarından girdi (`PromptTokenCount`, `ToolUsePromptTokenCount`) ve çıktı (`CandidatesTokenCount`, `ThoughtsTokenCount`) token'ları alınır.
- **Thinking token'ları:** fiyat sayfası çıktı fiyatını "thinking token'ları dahil" olarak veriyor (S-G2, 2026-10-18). Ledger, thinking token'larını çıktı fiyatıyla hesaplar.
- **Retry'ın tek kaynağı:** SDK'nın kendi retry'ı (`HttpOptions.RetryOptions`) kapatılır; retry yalnızca bizim politikamızla yapılır. Kapı B'de, sahte handler'daki istek sayısıyla test edilir.
- **Sürüm:** Kapı B başında, en az 14 gün önce yayımlanmış en yeni kararlı sürüm sabitlenir (2026-10-18'de bu sürüm 1.23.0, 2026-09-30). Geçişli bağımlılıklar `Directory.Packages.props`'a ve CHANGELOG'a yazılır.
- **SDK'dan vazgeçme koşulu:** Kapı B'de enjeksiyon, iptal ya da retry'ın kapatılması beklendiği gibi çalışmazsa adapter `HttpClient` ile doğrudan REST'e geçer ve ADR-0007 revize edilir. Bu adapter içi bir değişikliktir; arayüz değişmez.
- **`actualModel` ile `requestedModel` eşitliği:** `ModelVersion` ile yapılandırılmış model adı arasındaki eşleşme, görev politikasındaki kabul listesiyle yapılır. Listede olmayan değer degraded sayılır. Gerçek biçim Kapı C spike'ında doğrulanır.

### 3. Router modları ve sonuçlar
- **`Production`:** karar etkileyen bir görev yalnızca `qualifiedModels` içindeki bir (sağlayıcı, model, prompt sürümü, `qualificationId`) dörtlüsüne yönlendirilir. Liste boşsa çağrı yapılmaz ve `onUnavailable` uygulanır. Danışma görevleri nitelik gerektirmez; çıktıları doğrulanır ve etiketlenir.
- **`Qualification`:** yalnızca harness tarafından açılır ve kayıtlı, hash'i doğrulanmış bir `QualificationProtocol` ile `JobId` gerektirir. Bu modda nitelik şartı aranmaz, ama tüm tavanlar ve kill switch aynen geçerlidir. Bu modda üretilen çıktı hiçbir kapıya ya da geçişe girmez; yalnızca yeterlilik metriğine girer.
- **Sonuçlar:** `Completed | Abstained | ManualReview | DeterministicFallback | Stopped`. testing §6.3'teki `NoDecision`, refusal ya da şema dışı çıktı için kullanılan iç durumdur: bir onarım isteğinden sonra hâlâ geçersizse görev `onUnavailable`'a düşer ve `DecisionTrace`'e `NoDecision` nedeni yazılır.

### 4. Harcama, kill switch ve kalıcılık
- **Migration 0006** (`ai` şeması): `kill_switch_events`, `cost_ledger`, `decision_traces`, `qualification_protocols`, `task_qualifications`. Tablolar append-only'dir; trigger deseni migration 0005'tekiyle aynıdır. Aynı migration'da `research.hypotheses` eklenir ve `research.trials`'a `hypothesis_id` sütunu gelir.
- **Kill switch durumu:** en son `kill_switch_events` satırıdır. Açma otomatik ya da manuel olabilir; kapatma yalnızca CLI ile, gerekçe girilerek ve audit kaydıyla yapılır. Her çağrıdan ve her retry'dan önce okunur. Açıldığında bağlı `CancellationTokenSource` ile uçuştaki çağrı iptal edilir ve açık Batch işleri için `CancelBatch` çağrılır.
- **Ledger:** önce `EstimateMaxCost` kadar rezervasyon satırı, çağrı bitince gerçek maliyet satırı yazılır. Tavan kontrolü (aylık, iş başına, çağrı başına), gerçekleşen ve açık rezervasyonların toplamıyla tek bir işlem içinde yapılır.
- **Fiyat tablosu:** versiyonlu konfigürasyondur; değerler `sources.md` S-G2'den alınır. Model adları da konfigürasyondadır (T24).
- **Zaman:** harcama hızı penceresi, circuit breaker ve timeout'lar `TimeProvider` ile çalışır. Testlerde `Microsoft.Extensions.TimeProvider.Testing` kullanılır (ücretsiz NuGet paketi).
- **Jitter:** seed'li RNG ile üretilir; testler deterministiktir.

### 5. Anahtarlar ve gerçek çağrı koruması
- **API anahtarı:** kullanıcının kendi ortamında tutulur (`dotnet user-secrets` ya da kullanıcı oturumundaki ortam değişkeni). Claude Code anahtarı okumaz ve yazmaz. `.claude/settings.json`'a user-secrets dizini için Read deny kuralı eklenir ve `RepositoryRuleTests` bu kuralı doğrular.
- **Gerçek çağrılar:** yalnızca `AiCli spike|qualify --allow-network` ile yapılır. Bu komutlar etkileşimli onay ister ve yönlendirilmiş girdiyi reddeder (M1 Kapı-2 deseni). `--allow-network` bayrağı mevcut deny kuralıyla Claude Code'a kapalıdır. Böylece Kapı A ve B'de API çağrısı teknik olarak da engellenir.

## Gerekçe
- Adapter'ın ayrı bir projede olması, SDK'nın ve onun geçişli bağımlılıklarının (`Google.Apis.Auth`, `Microsoft.Extensions.AI.Abstractions`, `MimeTypes`) sistemin geri kalanına sızmasını önler ve sağlayıcı bağımsızlığını (ADR-0007) derleme düzeyinde korur.
- Researcher'ı ayrı bir projeye koymak, "SQL, shell ve web yok" iddiasını yalnızca çalışma zamanı kontrolüne değil, proje grafiğine ve kaynak taramasına da dayandırır.
- Append-only kill switch ve ledger, "yalnızca manuel kapatma" ve audit şartlarını DB düzeyinde sağlar.

## Riskler
- **Structured output ile tool'ların birlikte kullanımı:** önizleme durumunda ve yalnızca "Gemini 3 serisi" için belirtilmiş; 3.5 Flash-Lite açıkça anılmıyor (S-G7, 2026-10-18). Kapı C spike'ında doğrulanır. Çalışmazsa tool'suz, yalnızca structured output kullanılır.
- **Şema boyutu:** desteklenen JSON Schema alt kümesinin sayısal sınırları belgelenmemiş. DSL v1 şemasının sığıp sığmadığı spike'ta görülür.
- **SDK'nın hızlı sürüm döngüsü:** son 3 haftada 5 sürüm çıktı. Sürüm sabitlenir, güncelleme ayrı bir commit'le yapılır.
