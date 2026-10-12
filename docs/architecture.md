# Mimari

Son güncelleme: 2026-10-13 (rev. 3) · ADR'ler: [adr/](adr/) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

> Durum: Bu belgede anlatılan her şey **planlanmıştır, uygulanmamıştır**. Karar etiketleri (U/T/P/TBD) için bkz. `requirements.md`.

## 1. Ortamlar ve topoloji (T1, ADR-0009)

Kod tarafında modüler bir monolit (tek solution, ADR-0001). Çalışma zamanında iki ortam var:

```
GELİŞTİRME MAKİNESİ (Windows, Claude Code burada çalışır)
  - Kaynak kod, testler, Testcontainers
  - dev PostgreSQL: yalnızca holdout penceresi DIŞINDAKİ veri (DB CHECK + importçu koruması)
  - M4: Research.Host yerelde (holdout dışı veriyle yeterlilik ve araştırma)
  - YOK: holdout verisi, holdout ya da VPS kimlik bilgisi, VPS'e etkileşimsiz erişim
        │
        │ yalnızca insan tarafından, etkileşimli başlatılan deploy (T19)
        ▼
PROD VPS (M5'ten itibaren, D5). Host'a hiçbir container portu yayınlanmaz.
  net "ops"     (internet çıkışı var) : Trading.Host, Research.Host, ops PostgreSQL
  net "eval"    (internal)            : Research.Host, HoldoutEvaluator         ← dar API, submit-only token
  net "holdout" (internal)            : HoldoutEvaluator, holdout PostgreSQL (ayrı instance)
  net "egress"  (internet çıkışı var) : yalnızca HoldoutEvaluator (data.binance.vision'dan import)
  Trading.Host "eval" ağında değil → evaluator'a erişemez. Research.Host "holdout" ağında değil → holdout DB'sine erişemez.
  Dışa açık tek port: SSH (parolalı anahtar). Ops API ve dashboard'a SSH tüneliyle erişilir.
```

| Veri deposu | Konum | İçerik | Erişim |
|---|---|---|---|
| dev DB | Geliştirme makinesi | Holdout penceresi dışındaki piyasa verisi, deneyler (geliştirme amaçlı) | Geliştirici, testler, Claude Code |
| ops DB | VPS | Piyasa verisi (1. dönem holdout penceresi hiç import edilmez), deneyler, kayıt, emirler, ledger, audit, trace | Trading.Host ve Research.Host, modül başına DB rolleriyle. Research piyasa verisini yalnızca, ilan edilmiş tüm holdout pencerelerini dışlayan `research.candles` view'ı üzerinden okur |
| holdout DB | VPS, **ayrı bir PostgreSQL instance'ı** | Yalnızca holdout penceresindeki piyasa verisi, değerlendirme kayıtları | Yalnızca HoldoutEvaluator |

ADR-0001'deki "iki process yalnızca PostgreSQL üzerinden haberleşir" kuralı **ops DB için** geçerlidir. Holdout DB'si bu kuralın dışındadır ve ona yalnızca evaluator API'si üzerinden ulaşılır.

### Modüller
| Modül | Sorumluluk | Nerede |
|---|---|---|
| MarketData | Import, canlı mum çekme, doğrulama, `DatasetSnapshot`, holdout pencere koruması | Kernel ve host'lar |
| Indicators, Strategy (DSL), CostModel, Sizing | Saf ve deterministik hesaplar | Kernel (canlı sistem, backtest ve evaluator aynı kodu kullanır) |
| Backtest, Evaluation | Simülasyon, walk-forward, istatistik, kapılar | Kernel. Research.Host ve Evaluator tarafından kullanılır |
| Registry | Strateji sürümleri ve yaşam döngüsü | Research yazar, Trading okur (ops DB) |
| Research | Hipotezler, deney kuyruğu, deney hafızası, Researcher | Research.Host |
| ModelRouter | `IAiProvider`, görev politikaları, yeterlilik kaydı, bütçe, LLM acil durdurma, `DecisionTrace` | Her iki host |
| EvidenceValidator | LLM çıktısındaki referansları ve sayısal iddiaları doğrulama | Her iki host |
| HoldoutEvaluator | Holdout verisini import etme ve değerlendirme | Ayrı container |
| SourceGateway (M7) | Allowlist'li web erişimi | Research.Host |
| Portfolio, Allocation, Risk, Execution, Reconciliation, Ops/Audit | Operasyon | Trading.Host |

## 2. LLM ile deterministik kodun sınırı
| İş | Sahibi |
|---|---|
| İndikatörler, sinyaller, DSL, muhasebe, sizing, risk, emir doğrulama, reconciliation, kill switch'ler | **Deterministik** |
| Exit ve stop sinyalleri | **Deterministik.** LLM engelleyemez |
| Bant içinde sermaye dağıtımı, yeni girişleri veto etme | LLM (AllocationProposal, nitelik şartıyla) |
| Hipotez üretmek | LLM (HypothesisGeneration, nitelik şartıyla) |
| Web içeriğinden bulgu çıkarmak | LLM (FindingExtraction, tool'suz, nitelik şartıyla) |
| Sonuç yorumu ve özetler | LLM (danışma amaçlı, runtime doğrulamalı) |

LLM çıktısı her zaman güvenilmez girdidir: şema doğrulaması, clamp, DSL beyaz listesi, `EvidenceValidator` ve deterministik kapılardan geçer.

## 3. Model yönlendirme (D18, ADR-0007, ADR-0010)

### 3.1 Arayüzler
```
IAiProvider                       // sağlayıcı başına bir adapter; şu an yalnızca GeminiProvider
  Generate(AiRequest, NeutralSchema, NeutralToolSet, CancellationToken) -> AiResponse
  SubmitBatch / GetBatch / CancelBatch
  EstimateMaxCost(AiRequest) -> UsdAmount

ModelRouter
  Execute(TaskClass, TaskInput, JobId) -> TaskResult
  // TaskResult: Completed | Abstained | ManualReview | DeterministicFallback | Stopped

TaskPolicy (config, görev sınıfı başına)
  kind: DecisionAffecting | Advisory
  qualifiedModels: [ {provider, model, promptVersion, qualificationId} ]   // DecisionAffecting için zorunlu
  reasoningEffort, maxOutputTokens, timeout
  onUnavailable: DeterministicFallback | Abstain | ManualReview | Skip
```

- **Yetkili tek runtime sağlayıcısı:** Gemini (D12). Başka bir sağlayıcı ancak ayrı onayla ve yeni bir adapter olarak eklenir (D19).
- **Sağlayıcılar arası fallback yok.** Kalite açısından kritik bir görev, nitelikli olmayan bir modele düşürülmez (D20).
- Şemalar nötr ve sığdır: Gemini'nin desteklediği JSON Schema alt kümesine sığar. Zorunlu tool seçimine dayanılmaz.
- Konuşma geçmişi yalnızca eklenerek büyür. Thinking içeriği kararlarda kullanılmaz.
- Batch API ve context caching gibi sağlayıcıya özel optimizasyonlar adapter içinde kalır.

### 3.2 Görev sınıfları (T7)
| Görev sınıfı | Tür | Nitelik şartı | Nitelikli model yoksa |
|---|---|---|---|
| HypothesisGeneration | Karar etkileyen | Evet (M4) | LLM hipotezi üretilmez. Grid/parametrik arama ve insan onaylı hipotezlerle devam edilir |
| FindingExtraction | Karar etkileyen (injection yüzeyi) | Evet (M7) | Web araştırması kapalı |
| AllocationProposal | Karar etkileyen | Evet (M8, gölge mod) | Varsayılan deterministik politika (T11) |
| ResultInterpretation | Danışma | Hayır. Her çıktı `EvidenceValidator`'dan geçer | Yalnızca deterministik rapor |
| Summary/Formatting | Danışma | Hayır. Her çıktı doğrulanır | Şablon metin |

Danışma amaçlı çıktılar hiçbir kapıyı, geçişi ya da emri etkilemez ve etiketlenir.

### 3.3 DecisionTrace ve EvidenceValidator
- **`DecisionTrace`:** Her LLM görevi için kaydedilenler: `TaskId`, `JobId`, görev sınıfı, girdi hash'i, kanıt ID'leri, `requestedModel` ve `actualModel`, prompt sürümü, `qualificationId`, token sayıları, gecikme, tahmini ve gerçek maliyet, validator sonuçları, `TaskResult`. `actualModel ≠ requestedModel` ise sonuç **degraded** olarak işaretlenir ve hiçbir kapıdan geçmez.
- **`EvidenceValidator`:**
  - her iddianın `evidenceRef`'i var mı ve karar anında erişilebilir miydi;
  - sayısal iddialar kaynak değerle tolerans içinde eşleşiyor mu.

  Karar etkileyen görevlerde tek bir referanssız kritik iddia çıktıyı reddettirir.

### 3.4 Harcama kontrolü ve LLM acil durdurma (T9, ADR-0010)
| Katman | Ne yapar | Değer |
|---|---|---|
| Gemini kredisi (12.000 TL) | Finansman kaynağı. **Harcama onayı değildir** | Kapsamı doğrulanmadı (B1) |
| Aylık tavan (D4) | Ledger'da gerçekleşen ve rezerve edilen toplam. Aşılacaksa çağrı yapılmaz | Başlangıç **10 USD** (T9), D4 aralığı içinde |
| İş başına tavan | Bir yeterlilik koşusu ya da araştırma döngüsü (`JobId`) | Varsayılan: aylık tavanın %30'u (yeterlilik), %5'i (araştırma döngüsü). Başlangıç değerleri |
| Çağrı başına tavan | `EstimateMaxCost` = girdi token'ları × fiyat + `maxOutputTokens` × fiyat. Rezervasyon olarak düşülür | `maxOutputTokens` görev politikasında |
| Eşzamanlılık | Uçuştaki en fazla çağrı sayısı | Başlangıç 1 (M4'te ölçülüp ayarlanır) |
| Retry | 429, 5xx ve ağ hatasında en fazla 2 deneme; üstel bekleme + jitter; `Retry-After`'a uyulur. 4xx'te retry yok. Şema hatası: tek bir onarım isteği, sonra `onUnavailable` | Başlangıç değerleri |
| Timeout | Çağrı başına | Başlangıç 120 sn (Batch hariç) |
| **LLM acil durdurma** (`AiKillSwitch`) | ops DB'de kalıcı bir bayrak. Her çağrıdan ve her retry'dan önce kontrol edilir. Açıldığında: yeni çağrı yapılmaz, uçuştaki çağrılar `CancellationToken` ile iptal edilir, açık Batch işleri iptal edilir, görevler `onUnavailable`'a düşer | — |
| Otomatik tetikler | (a) aylık tavana ulaşılması; (b) son 60 dakikadaki harcamanın aylık tavanın %20'sini aşması; (c) gerçek maliyetin tahmini 2 katından fazla olması (art arda 3 çağrıda); (d) circuit breaker'ın açılması | Başlangıç değerleri |
| Kapatma | Yalnızca manuel (Ops API veya CLI), audit kaydıyla | — |
| Bant dışı durdurma | Runbook: Google konsolunda API anahtarını iptal etme veya yenileme. Kodumuzdan bağımsız son çare | — |
| Sağlayıcı tarafı limitler | Tier 1'in 250 $ fatura tavanı ve 10 dakikada 10 $ sınırı (S-G4) | Hesabınıza uygulanışı **doğrulanmadı.** İkinci katman, birincil kontrol değil |

- Ledger USD cinsinden, `sources.md`'deki liste fiyatlarıyla tutar. Google faturasıyla (TL) **aylık manuel mutabakat** yapılır. Fark %10'u aşarsa fiyat tablosu güncellenir ve olay audit'e yazılır.
- **Ayrı API anahtarları:** geliştirme makinesi ile prod VPS için ayrı anahtarlar (tercihen ayrı Google projeleri). Geliştirme makinesindeki anahtar, Claude Code tarafından kodumuzu atlayarak kullanılabilir. Bu harcamayı yalnızca Google tarafındaki limitler ve aylık mutabakat yakalar. Kabul edilmiş artık risk.
- **Ücretli katman (T10):** Yeterlilik ve runtime kullanımı ücretli katmanda yapılır. Ücretsiz katman yalnızca sentetik içerikli spike içindir.

### 3.5 Maliyet tahmini (varsayıma dayalı)
`gemini-3.5-flash-lite` fiyatları (S-G2, 2026-10-12'de okundu, değişebilir): girdi 0,30 $, çıktı 2,50 $ (1M token başına); Batch ile 0,15 $ / 1,25 $. Önceki kullanım senaryoları aylık birkaç dolar seviyesinde, 10 USD'lik başlangıç tavanının altında. Gerçek profil M4'te ölçülür. Thinking token'larının nasıl faturalandığı doğrulanmadı.

### 3.6 Geliştirmede Claude Code ve runtime ayrımı
Claude Code yalnızca geliştirme aracıdır. Runtime erişimi sağladığı varsayılmaz (D13) ve uygulamanın AI arka ucu olarak headless çalıştırılması kapsam dışıdır. Geliştirmede Claude Code ile üretilen hipotezler normal hattan geçer ve `TrialFamily` sayacına girer (T18).

## 4. Agent rolleri
| Rol | Host | Tool'lar | Erişemedikleri |
|---|---|---|---|
| Researcher | Research | `ListIndicators`, `SearchExperiments`, `GetDatasetCatalog` (holdout penceresi listelenmez), `ValidateStrategyDefinition`, `SubmitExperiment` (bütçeye ve deneme sayısına tabi), `GetExperimentResult`; M7'de `SearchFindings`, `RequestSourceResearch` | Holdout, evaluator API'si, SQL, shell, ham web, emir, risk config |
| Extractor (M7) | Research | Yok | Hepsi |
| Allocator (M8) | Trading | Salt okunur portföy, sinyal, piyasa ve risk özetleri | Web, emir, risk config, araştırma verisi |

Holdout değerlendirmesini hiçbir LLM rolü başlatamaz. `Candidate → HoldoutTested` geçişini deterministik Registry yürütür (§6).

## 5. Operasyonel döngü (her saat)
1. MarketData kapanan mumu çeker ve doğrular. Veri bayatsa yalnızca çıkışlar çalışır.
2. Reconciliation çalışır. Tutarsızlık varsa yeni giriş açılmaz.
3. StrategyEngine sinyalleri üretir.
4. **Varsayılan allocation politikası (T11)** hedef ağırlıkları hesaplar. M8'den sonra ve nitelikli bir model varsa Allocator bu ağırlıkları bant içinde ayarlayabilir. Her durumda sonucu deterministik Sizer kullanır.
5. Risk her intent için onay, kırpma ya da ret kararı verir.
6. Execution önce `clientOrderId`'yi kaydeder (outbox), sonra emri gönderir.
7. Dolumlar işlenir, ledger ve audit güncellenir.

```
Intent → RiskApproved | RiskRejected
RiskApproved → Submitting → Accepted → PartiallyFilled → Filled | Canceled | Expired | ExchangeRejected
Submitting --timeout/5xx--> Unknown --(origClientOrderId ile sorgu)--> gerçek durum
```

## 6. Araştırma, değerlendirme ve holdout

### 6.1 Veri bölümleri
| Bölüm | İçerik | Erişen |
|---|---|---|
| Development + validation | Holdout penceresi dışındaki tüm geçmiş veri | Research (walk-forward pencereleri) |
| Holdout (dönem `e`) | `[H_start(e), H_end(e)]` | Yalnızca HoldoutEvaluator |
| Forward (paper) | Canlı veri | Trading. Sonuçlar Research tarafından okunur |

### 6.2 Holdout güven sınırı (D17, T2, ADR-0009)
**Tehdit modeli:**
- **Önlenen:** iş akışı ve araç kaynaklı kontaminasyon. Holdout verisinin geliştirme ya da araştırma DB'sine, deneylere veya tuning'e girmesi; geliştirme ortamının (Claude Code'un komutları dahil) holdout DB'sine ya da evaluator'a erişmesi; tekrarlı değerlendirmelerle holdout'a uydurma.
- **Önlenemeyen (kabul edilmiş artık risk):** Veri kamuya açık. Bir insan ya da geliştirme ortamı veriyi internetten doğrudan indirebilir. Buna karşı süreç kuralı, Claude Code korkulukları (T17) ve audit uygulanır. İzolasyon gizlilik sağlamaz.

**Kontroller ve sorumlulukları:**
| Katman | Kontrol | Neyi garanti eder | Test |
|---|---|---|---|
| Veri bütünlüğü | **dev DB:** mum tablosunda ilan edilmiş tüm holdout pencerelerini dışlayan `CHECK` kısıtı (zaman damgası normalize edildikten sonra). **Her iki ortam:** importçu, REST boşluk doldurucu ve dosya listesi, 1. dönem penceresiyle kesişen istekleri reddeder; sınır ayı için günlük dosyalar kullanılır (T6). ops DB'sinde `CHECK` yalnızca 1. dönem için var, çünkü ileriye dönük pencereler Trading'in canlı verisiyle çakışır | Uygulama yoluyla, kazara holdout verisi yazılamaz; holdout zip'i geliştirme diskine inmez | H1, H2, H2b, H3 |
| Yetkilendirme | Holdout DB'sinin kimlik bilgisi yalnızca evaluator container'ının secret'ında. Research.Host'ta yalnızca **submit-only** bir evaluator token'ı var. ops DB'sinde Research rolünün `market.candles` üzerinde yetkisi yok; yalnızca pencereleri dışlayan `research.candles` view'ına `SELECT` hakkı var | Research ne holdout DB'sini ne de ileriye dönük pencere verisini okuyabilir. Evaluator API'si veri döndürmez | H4, H6, H6b, H10 |
| Ağ | Dört Docker ağı (§1): holdout DB'si yalnızca `holdout` (internal) ağında; evaluator'a yalnızca `eval` (internal) ağından erişilir ve bu ağda Research.Host dışında kimse yok. Hiçbir container portu host'a yayınlanmaz. VPS'te dışa açık tek port SSH | Ne geliştirme makinesinin, ne Trading.Host'un, ne de Research.Host'un holdout DB'sine ağ yolu var | H5, H6, H14 |
| Yönetim erişimi | VPS SSH anahtarı parolalı ve ssh-agent'a yüklenmez. Claude Code'un etkileşimsiz komutları bu anahtarı kullanamaz (T19). Deploy ve yönetim yalnızca etkileşimli olarak insan tarafından yapılır | Claude Code geliştirme makinesinden VPS'e erişemez | H13 |
| Süreç | Proje talimat dosyası ve izin kuralları (T17); evaluator kodunda değişiklik deploy kontrol listesinde vurgulanır | Kasıtlı olmayan ihlallerin azalması | Gözden geçirme |
| Audit | Her holdout isteği ve sonucu append-only olarak kaydedilir | İzlenebilirlik | H11 |

### 6.3 Holdout değerlendirme akışı ve politikası (T3–T5, ADR-0006)
1. **Ön koşul:** `HoldoutEnvironmentVerified = true`. Bu bayrak yalnızca H-ENV testleri gerçek ortamda geçtikten sonra, Ops API üzerinden ve kullanıcı oturumuyla set edilir; evaluator altyapısında ya da kodunda her değişiklikte sıfırlanır.
2. Registry, `Candidate` durumundaki adayı seçer. Adayın araştırma değerlendirmesi, evaluator'ın çalıştırdığı **aynı engine SHA**'sıyla yapılmış olmalıdır; değilse aday önce mevcut engine ile yeniden değerlendirilir.
3. Research.Host evaluator'a `POST /evaluations { strategyDefinition (DSL JSON), definitionHash, familyId, engineSha }` gönderir.
4. **Evaluator'ın kontrolleri:**
   - `definitionHash`'i doğrular ve `familyId`'yi yapı hash'inden kendisi yeniden hesaplar;
   - `engineSha` kendi sürümüyle eşleşiyor mu;
   - aile bu dönemde daha önce değerlendirildi mi;
   - dönem kotası K doldu mu.

   Herhangi bir kontrol başarısızsa istek reddedilir ve audit'e yazılır.
5. Evaluator değerlendirmeyi holdout verisi üzerinde çalıştırır. Yanıt **yalnızca şunları** içerir: geçti/kaldı; 2 anlamlı basamağa yuvarlanmış net getiri, Sharpe, maksimum drawdown, işlem sayısı ve birincil test istatistiği; düzeltilmiş p-değeri; `evaluatorVersion` (image digest ve engine SHA); `epochId`; holdout veri setinin hash'i. Zaman serisi, işlem listesi ya da mum verisi döndürülmez.
6. **Geçme kuralı:** önceden kayıtlı birincil test (maliyetler düşülmüş getirinin benchmark'a karşı block-bootstrap testi), dönem içinde yapılan değerlendirme sayısına göre Holm düzeltmesiyle uygulanır. α TBD (§8).
7. **Kalan aile** bu dönemde tekrar değerlendirilemez. Parametre varyantları da aynı yapı hash'ine sahip olduğu için aynı ailedir.
8. **Evaluator sürüm değişikliği:** Önceki sonuçlar kendi sürümleriyle geçerli kalır. Bir aileyi yeni sürümle tekrar değerlendirmek **kotadan düşer** ve aynı aile kuralına tabidir (yani dönem içinde mümkün değildir).
9. **Dönem yenileme (T4):** Kota dolunca, yeni dönem **ileriye dönük** ilan edilir: `[T, T + 9 ay]`.
   - `T` anından itibaren bu pencere dev DB'sinde `CHECK` ile, ops DB'sinde ise `research.candles` view'ı ile Research'ten gizlenir. Trading canlı veriyi kullanmaya devam eder.
   - Evaluator, pencere kapandıktan sonra veriyi doğrudan kaynaktan (checksum'la) kendi DB'sine import eder.
   - Dönem `T + 9 ay`'da değerlendirmeye açılır. Bu süre boyunca terfi kanıtı paper trading'den gelir.
   - **Kabul edilmiş artık risk:** Bu süre zarfında Research, **mevcut** stratejilerin paper sonuçlarını görür. Bu, pencere hakkında dolaylı ve zayıf bir bilgi sızıntısıdır. Yeni dönemin yalnızca yeni aileler için kullanılması bu riski sınırlar.

### 6.4 Kapılar ve yaşam döngüsü
- **Kapılar** (eşikler TBD, M3'te kalibre edilir): maliyetler düşülmüş getiri; minimum işlem sayısı ve bootstrap güven aralığı; benchmark karşılaştırması; walk-forward tutarlılığı; parametre komşuluğu; çoklu varlık; `TrialFamily` deneme sayısıyla düzeltilmiş Deflated Sharpe.
- **`TrialFamily` (T18):** DSL normalize edilir ve sayısal parametreler çıkarılır; kalan yapının hash'i aile kimliğidir. Deneme sayacı, menşeinden bağımsız olarak (LLM, grid, geliştirmede Claude Code) tüm denemeleri sayar.

| Geçiş | Yetki |
|---|---|
| `Hypothesis → Defined` | Researcher ya da insan (DSL doğrulaması şart) |
| `Defined → Backtested → RobustnessEvaluated → Candidate` | Otomatik (kapılar) |
| `Candidate → HoldoutTested` | Otomatik. **`HoldoutEnvironmentVerified` gerekir.** Aile başına dönemde bir kez, kotaya tabi |
| `HoldoutTested → PaperTrading → PaperEvaluated` | Otomatik (D3) |
| `PaperEvaluated → Active (Testnet)` | **Kullanıcı onayı** |
| `* → Rejected / Retired` | Otomatik, gerekçeli |

## 7. Domain varlıkları (özet)
- **Veri:** `TradingPair`, `Candle`, `DatasetSnapshot`.
- **Holdout:** `HoldoutEpoch` (pencere, durum, kota K, α), `HoldoutEvaluationRequest`, `HoldoutEvaluationResult`, `EvaluatorRelease` (image digest, engine SHA, deploy zamanı).
- **Araştırma:** `IndicatorSpec`, `StrategyDefinition`, `TrialFamily`, `Hypothesis` (menşei: `llm` / `grid` / `human` / `dev-assisted`), `Experiment`, `ExperimentResult`, `StrategyLifecycleRecord`.
- **Operasyon:** `Signal`, `TargetAllocation`, `AllocationProposal`, `RiskDecision`, `OrderIntent`, `Order`, `Fill`, `Position`, `LedgerEntry`, `BalanceSnapshot`, `ReconciliationRun`.
- **AI:** `TaskQualification`, `QualificationProtocol` (hash'li), `DecisionTrace`, `CostLedgerEntry`, `AiKillSwitchState`.
- **Denetim:** `RiskConfig`, `KillSwitchState`, `AuditEvent`.

## 8. Kontrollü web araştırması (M7, ADR-0008)
- SourceGateway deterministiktir: allowlist (redirect'ler dahil), robots.txt, domain başına rate limit, önbellek (URL ve içerik hash'i; varsayılan TTL 30 gün), temizleme ve `SourceDocument` kaydı (URL, zaman, hash, güven seviyesi).
- Extractor **tool'suz** çalışır ve yalnızca FindingExtraction için nitelikli bir model varsa devreye girer. Çıktısı `Finding[]` şemasıyla sınırlıdır. Alıntılar, kendi çıkardığımız metinde deterministik olarak aranarak doğrulanır.
- Researcher ham metni görmez. Bulgular yalnızca hipotez üretir; hattın hiçbir adımı atlanamaz.
- **Google Search grounding ve URL context otonom hatta kullanılmaz (T13).** Grounding şartları (görüntüleme, saklama, "learn from" yasağı; S-G5) kalıcı otonom hafızayla bağdaşmıyor. URL context, getirmeyi Google'a bıraktığı için redirect allowlist'ini, içerik hash'ini ve alıntı doğrulamasını devre dışı bırakır; aynı veriyi kendi getiricimiz zaten sağlıyor. Etkileşimli grounding modu: P2.
- Kaynak güven seviyeleri (T1: arXiv q-fin, resmi dokümantasyon; T2: kullanıcının tek tek onayladığı kaynaklar) yalnızca önceliği belirler; kanıt değeri taşımaz. Allowlist'i yalnızca kullanıcı değiştirir.

## 9. Hata yönetimi
Bkz. `risk-model.md` §4.

## 10. Güvenlik
- Exchange anahtarı: trade izni, withdrawal yok, IP kısıtı (VPS IP'si). Yalnızca Trading.Host'ta bulunur.
- **Secret'lar:**
  - Geliştirmede `dotnet user-secrets` (yalnızca geliştirme anahtarları; holdout ve VPS secret'ı yok).
  - VPS'te Docker secret dosyaları (root'a ait, 0400).
  - Holdout DB parolası yalnızca evaluator secret'ında.
  - Research.Host'ta yalnızca submit-only evaluator token'ı.
- **Ağ:** VPS'te dışa açık tek port SSH (parola girişi kapalı, anahtarlı). Ops API ve dashboard'a SSH tüneliyle erişilir. `eval` ve `holdout` ağları `internal` (§1).
- **Claude Code korkulukları (T17):**
  - Proje talimat dosyası: holdout penceresine ait veri indirilmez ya da incelenmez; holdout korumaları ve evaluator kodu, açık kullanıcı isteği olmadan değiştirilmez.
  - Proje izin kuralları: VPS host'una SSH ve holdout sınır ayı dosyalarına erişim için deny kuralları.

  Bunlar ek bir savunma katmanıdır; güven sınırı §6.2'deki kontrollerdir.
- Log redaction ve kanarya testleri.
- Audit tablosunda uygulama rolleri yalnızca `INSERT` yetkisine sahiptir (`UPDATE`/`DELETE` yok).

## 11. Gözlemlenebilirlik (T15)
- Yapılandırılmış JSON loglar (`Microsoft.Extensions.Logging` ve JSON console formatter). Her satırda `CycleId`, `JobId`, `TaskId`, `OrderId` gibi correlation ID'leri.
- Health endpoint'leri (`/health/live`, `/health/ready`). Hazırlık; DB, son mumun tazeliği ve kill switch durumlarını kapsar.
- Metrikler ops DB'de sorgulanabilir tablolar olarak tutulur (döngü süreleri, gecikme, maliyet). Prometheus/Grafana/ELK yok; ihtiyaç doğarsa M10'da yeniden değerlendirilir.
- Alarmlar M10'da.

## 12. Dağıtım
- **Geliştirme:** docker-compose (dev PostgreSQL ve host'lar). Testcontainers.
- **Prod (M5, D5):** tek küçük VPS (2 vCPU / 4 GB sınıfı, fiyat doğrulanmadı), docker-compose ile iki ağ.
  - Deploy: geliştirme makinesinde image derlenir ve image digest'i kaydedilir; ardından `docker save | ssh vps docker load` **etkileşimli** olarak çalıştırılır (T19). Registry yok.
  - Yedek: ops DB için günlük, şifreli `pg_dump` ve harici bir kopya. Holdout DB'si yeniden üretilebilir olduğu için (kaynaktan ve checksum'la) yedeklenmez; yalnızca değerlendirme kayıtları ayrıca yedeklenir.

## 13. Reddedilen alternatifler
| Alternatif | Neden |
|---|---|
| Mikroservisler, broker, Redis, TimescaleDB | Gereksiz (ADR-0002) |
| M3'te ayrı bir holdout VPS'i | M3'te gerçek bir aday yok. Maliyeti öne çeker. Evaluator sentetik veriyle geliştirilebilir (ADR-0009) |
| Holdout'u geliştirme makinesinde ayrı bir OS kullanıcısında tutmak | Windows'ta yönetici veya Docker erişimi olan biri ya da bir araç sınırı aşabilir (ADR-0009) |
| Holdout değerlendirmesini CI'da yapmak | Workflow geliştirme ortamından değiştirilebilir; uzak repo gerektirir (ADR-0009) |
| Harici image registry | Ek hesap ve kimlik bilgisi; tek VPS için gereksiz |
| Sağlayıcılar arası LLM fallback'i | Kalite eşdeğerliği varsayımı (D20) |
| LLM'in kod üretmesi, mum başına LLM kararı | ADR-0003, ADR-0005 |
| Grounding ve URL context ile otonom araştırma | §8 |
| Prometheus/Grafana/ELK | Tek kullanıcı için gereksiz işletim yükü |
