# Mimari

Son güncelleme: 2026-10-14 (rev. 5) · ADR'ler: [adr/](adr/) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

> Durum: M0'da çözüm iskeleti, migration altyapısı, redaction'lı JSON loglama, mimari testler ve Claude Code korkulukları uygulandı (sonuçları CHANGELOG'da). Bu belgede anlatılan geri kalan her şey **planlanmıştır, uygulanmamıştır**. Karar etiketleri (U/T/P/TBD) için bkz. `requirements.md`.

## 1. Ortam ve topoloji (T1, ADR-0013)

Kod tarafında modüler bir monolit (tek solution, ADR-0001). Çalışma zamanında **tek bir yerel makine** var (D5 rev.: VPS yok). Kullanıcının Windows + Docker Desktop bilgisayarı, 16 GB RAM; Claude Code da aynı makinede çalışır.

```
YEREL MAKİNE (Windows + Docker Desktop, 16 GB RAM)
  Geliştirme: kaynak kod, testler, Testcontainers
  Docker Compose (her servis için mem_limit ve cpus; ADR-0013 §3):
    net "ops"     (internet çıkışı var) : Trading.Host (sürekli), Research.Host (iş bazlı), PostgreSQL
    net "eval"    (internal)            : Research.Host, HoldoutEvaluator   ← dar API, submit-only ve read-status token'ları
    net "holdout" (internal)            : HoldoutEvaluator (iş bazlı), PostgreSQL
    net "egress"  (internet çıkışı var) : yalnızca HoldoutEvaluator (data.binance.vision'dan import)
  PostgreSQL: TEK instance; ayrı veritabanları "ops" ve "holdout" (M0–M2'de yalnızca geliştirme veritabanı)
    "holdout" veritabanına yalnızca eval_* rolleriyle ve yalnızca "holdout" ağından bağlanılır (pg_hba)
  StatusRelay ayrı container DEĞİL (D24): HoldoutEvaluator, status_relay rolüyle research.cohort_status'a ekleme yapar.
  Trading.Host "eval" ağında değil → evaluator'a erişemez. Research.Host "holdout" ağında değil → holdout veritabanına erişemez.
  Host'a yalnızca 127.0.0.1 (loopback) yayınlanır: geliştirme veritabanı portu, Ops API ve dashboard. SSH yok.
  Kabul edilmiş artık risk: geliştirici ve Claude Code holdout ile aynı makinede (D17 rev., ADR-0013).
```

| Veri deposu | Konum | İçerik | Erişim |
|---|---|---|---|
| Geliştirme veritabanı (`portfolio_dev`) | Yerel PostgreSQL instance'ı | `market.candles` (yalnızca kesim tarihinden önce), deneyler (geliştirme amaçlı) | Geliştirici, testler, Claude Code |
| `ops` veritabanı (M5) | Aynı instance | `market.candles` (araştırma verisi, kesimden önce), `market.live_candles` (canlı veri; yalnızca Trading), deneyler, kayıt, emirler, ledger, audit, trace | Modül başına DB rolleri (§6.2). Research yalnızca `research.*` view'larını okur |
| `holdout` veritabanı (M5; M3'te sentetik) | **Aynı instance, ayrı veritabanı**; veri yalnızca Docker volume'ünde | Tanısal ve ileriye dönük pencere verisi (evaluator'ın kendi import'u), manifestolar ve sonuçlar | Yalnızca evaluator rolleri; `CONNECT` yalnızca `eval_*` rollerine; `pg_hba` ile yalnızca `holdout` ağından |

ADR-0001'deki "iki process yalnızca PostgreSQL üzerinden haberleşir" kuralı **`ops` veritabanı için** geçerlidir. `holdout` veritabanı bu kuralın dışındadır ve ona yalnızca evaluator API'si üzerinden ulaşılır.

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
- **Ayrı API anahtarları:** geliştirme ve runtime için ayrı anahtarlar (tercihen ayrı Google projeleri). Yerel topolojide (ADR-0013) ikisi de Claude Code ile aynı makinededir; anahtarlar kodumuzu atlayarak kullanılabilir. Bu harcamayı yalnızca Google tarafındaki limitler ve aylık mutabakat yakalar. Kabul edilmiş artık risk.
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

Hiçbir LLM rolü dondurma, holdout değerlendirmesi ya da doğrulama durumu üzerinde yetkiye sahip değildir. Kohort işlemlerini deterministik Registry ve evaluator yürütür (§6).

## 5. Operasyonel döngü (her saat)
1. MarketData kapanan mumu çeker ve doğrular. Veri bayatsa yalnızca çıkışlar çalışır.
2. Reconciliation çalışır. Tutarsızlık varsa yeni giriş açılmaz.
3. **Koruyucu stop kontrolü (ADR-0014):** paper'da kapanan muma OHLC stop kuralı uygulanır (`Open ≤ S` ise `Open`'dan, değilse `Low ≤ S` ise `S`'den çıkış). Testnet'te stop borsada emir olarak bekler; bu adımda yalnızca dolumlar mutabakata alınır.
4. StrategyEngine kapanış sinyallerini üretir. Giriş ve çıkış sinyalleri bir sonraki mumun açılışında uygulanır.
5. **Varsayılan allocation politikası (T11)** hedef ağırlıkları hesaplar. M8'den sonra, nitelikli bir model varsa Allocator bu ağırlıkları bant içinde ayarlayabilir; **ama yalnızca `Validated` stratejiler için.** Her durumda sonucu deterministik Sizer kullanır.
6. Risk her intent için onay, kırpma ya da ret kararı verir. Yeni pozisyonların efektif stop seviyesi (strateji stop'u ve `RiskConfig` hard stop'undan girişe en yakın olanı, D25) burada belirlenir. Deneysel paritelerin (E1/E2) toplam maruziyeti `ExperimentalPairsMaxExposure` (%10) tavanına tabidir (`risk-model.md` §2). Risk motoru doğrulama durumundan ve holdout yaşam döngüsünden **bağımsızdır.**
7. Execution önce `clientOrderId`'yi kaydeder (outbox), sonra emri gönderir. Testnet'te giriş dolumundan sonra koruyucu stop emri de aynı yoldan gönderilir. Emirler strateji ve kohort kimliğiyle etiketlenir (ADR-0015).
8. Dolumlar işlenir, ledger ve audit güncellenir.

```
Intent → RiskApproved | RiskRejected
RiskApproved → Submitting → Accepted → PartiallyFilled → Filled | Canceled | Expired | ExchangeRejected
Submitting --timeout/5xx--> Unknown --(origClientOrderId ile sorgu)--> gerçek durum | NeverAccepted
Accepted | PartiallyFilled --iptal--> CancelRequested --> Canceled | Filled | Unknown
(Testnet ayrıntıları: m6-protocol.md §2–§5, ADR-0021)
```

## 6. Araştırma, değerlendirme ve holdout (ADR-0006, ADR-0009, ADR-0012, ADR-0013)

### 6.1 Zaman semantiği ve veri dönemleri
- **Zaman:** Tüm zaman damgaları UTC anlarıdır (DB'de `timestamptz`, kodda offset 0 olan `DateTimeOffset`). DB oturum saat dilimi `UTC`'dir ve yerel saat dönüşümü yapılmaz. Aralıklar yarı açıktır: `[başlangıç, bitiş)`.
- **Mum üyeliği:** Açılışı `o`, aralığı `Δ` olan bir mum `[S, E)` dönemine ancak `S ≤ o` **ve** `o + Δ ≤ E` ise aittir; yani mumun tamamı dönemin içinde olmalıdır. Örneğin 2025-12-29 açılışlı haftalık mum 2026-01-04'te kapandığı için araştırma dönemine dahil değildir.
- **Araştırma kesim tarihi** `C = 2026-01-01T00:00:00Z` (sabit). İlerletilmesi yalnızca kullanıcı kararıyla mümkündür; en son **kapanmış** ileriye dönük pencerenin bitişini aşamaz ve açık bir kohortun penceresine giremez.

| Dönem | Aralık | Erişen | Niteliği |
|---|---|---|---|
| Araştırma (R) | `[2022-01-01, C)` (çekirdek); E1 için `[U0, C)` (ADR-0012) | Research, backtest, LLM rolleri | Tek araştırma veri seti |
| Tanısal holdout (D_c) | `[C, A_c)` | Yalnızca `eval_diagnostic` | **Kör değil** (bilgi kontaminasyonu). Yalnızca **eler**, terfi ettirmez |
| İleriye dönük birincil holdout (P_c) | `[A_c, A_c + L_c)` | Yalnızca `eval_prospective`, pencere kapandıktan sonra | Veri dondurmadan sonra oluşur. **Doğrulamanın tek kaynağıdır** |

`A_c` = `registered_at` + 24 saatten sonraki ilk UTC gece yarısı. Tek yetkili kaynak, manifestonun yetkili kayda eklendiği an olan `registered_at`'tir (U, 2026-10-14); dondurma anı `F_c` pencere hesabında kullanılmaz. Kohort 0 istisnası §6.3'te. `L_c` §6.5'e göre belirlenir.

### 6.2 Saklama, erişim, değerlendirme ve açıklama
**Saklama.** İleriye dönük pencerenin verisi, pencere bitmeden de **ops DB'sinde canlı veri olarak bulunur.** Koruma verinin yokluğuna değil, erişim kontrolüne dayanır.

| Tablo / depo | İçerik | Yazan |
|---|---|---|
| `market.candles` (dev ve ops) | Arşivden import edilen araştırma verisi. **Her ortamda** `CHECK (open_time + interval ≤ C)` | `market_import` |
| `market.live_candles` (ops) | Canlı mumlar (kesim sonrası) | Yalnızca `trading_app` |
| `trading.*` (ops) | Paper ve Testnet emirleri, dolumlar, PnL | `trading_app` |
| holdout DB `diagnostic.*` | Tanısal pencere verisi (evaluator'ın kendi import'u) | `eval_diagnostic` |
| holdout DB `prospective.*` | P_c verisi; **yalnızca pencere kapandıktan sonra** import edilir | `eval_prospective` |
| holdout DB `results.cohort_manifests` | Manifestolar. `registered_at` ve `A_c` **sunucu tarafından** atanır; append-only | Evaluator'ın kayıt yolu (`eval_registry`) |
| holdout DB `results.diagnostic_results` | Tanısal sonuçlar. Sonuç alanı **yalnızca `NotRejected` ya da `Rejected`** olabilir (DB `CHECK`); append-only | Yalnızca `eval_diagnostic` |
| holdout DB `results.prospective_runs` ve `results.prospective_results` | İleriye dönük koşular ve sonuçlar; append-only | Yalnızca `eval_prospective` |
| ops DB `research.cohort_status` | Kohort durumu (`sealed` / `revealed`) ve açığa çıkmış yuvarlanmış özet; append-only | Yalnızca `status_relay` |

**Evaluator'ın veri edinimi.** Evaluator'ın `eval_*` rolleri ops DB'sine erişemez ve evaluator canlı veriyi kullanmaz; ops DB'ye tek bağlantısı, `status_relay` rolüyle `research.cohort_status`'a yaptığı eklemedir (D24). Pencere verisini, araştırma verisiyle aynı kanonik kaynaktan (Binance açık veri arşivi, checksum doğrulamalı) kendisi import eder. Bunu `A_c + L_c` artı 3 günlük yayın tamponundan (başlangıç değeri) sonra yapar. Isınma verisi de aynı yoldan gelir.

**Kimlikler ve izinler:**

| Kimlik | Okuyabilir | Okuyamaz |
|---|---|---|
| `research_app` | `research.candles`, deneyler, `research.cohort_status` (**yalnızca okuma**), `research.revealed_cohorts` ve `research.revealed_paper_summary` (bu view'lar, `cohort_status`'ta `revealed` olan kohortları gösterir) | `market.live_candles`, `trading.*`, holdout DB'si. `cohort_status`'a **yazamaz** |
| `status_relay` (rev. 5: kimliği HoldoutEvaluator tutar, D24) | Hiçbir tablo | Başka hiçbir tablo. Yalnızca `ops` veritabanındaki `research.cohort_status`'a ekleme yapabilir; `pg_hba` ile yalnızca `holdout` ağından bağlanır |
| `trading_app` | `market.live_candles`, `trading.*`, aktif strateji tanımları (yalnızca okuma) | holdout DB'si, araştırma tabloları |
| `ops_api` (kullanıcı) | Paper ve Testnet izlemesi (açık kohortlar dahil), kohort durumları, açığa çıkmış tam raporlar | holdout DB'sinin ham verisi |
| `eval_registry` | Manifesto kaydı ve sınırların (açık kohort, aile kotası, K_D) uygulanması | Pencere verisi |
| `eval_diagnostic` | `diagnostic.*`; `results.diagnostic_results`'a yalnızca ekleme | `prospective.*`, `results.prospective_*`, ops DB'si |
| `eval_prospective` | `prospective.*`; `results.prospective_*`'a yalnızca ekleme | `diagnostic.*`, `results.diagnostic_results`'a yazma, ops DB'si |
| Yerel makine yöneticisi ve Docker yetkisi (insan ve Claude Code) | Teknik olarak her şey (`docker exec`, volume, superuser) | Kabul edilmiş artık risk (ADR-0013); `CLAUDE.md`, deny kuralları, süreç kuralı ve audit |

**Tehditler ve engeller (en az iki katman):**

| Tehdit | Engel |
|---|---|
| Erken değerlendirme | Evaluator zamanlayıcısı, enjekte edilebilir saatle `now < A_c + L_c + tampon` iken çalışmaz. "Şimdi değerlendir" API'si yoktur. DB trigger'ı, penceresi kapanmamış bir kohort için **yalnızca** `prospective.*`, `results.prospective_runs` ve `results.prospective_results` tablolarına `INSERT`'i reddeder. Manifesto ve tanısal tablolar bu trigger'ın kapsamında değildir |
| Geçmiş tarihli manifesto | `registered_at` (DB `now()`) ve `A_c` (`registered_at` + 24 saatten sonraki ilk UTC gece yarısı) sunucu tarafından atanır. İstemcinin gönderdiği değer reddedilir. `CHECK (A_c > registered_at)` |
| Sınırların atlatılması | Açık kohort sınırı, aile kotası ve K_D, yetkili kaynak olan holdout DB'sinde `eval_registry` tarafından kayıt anında, işlem içinde uygulanır. Registry'deki kontrol yalnızca erken uyarıdır |
| Yetkisiz import | Importçu yalnızca kayıtlı kohort pencerelerini import eder; serbest tarih aralığı kabul etmez. Aynı DB trigger'ı ve host allowlist'i |
| Erken açıklama | Sonuç pencere bitmeden hesaplanamaz. Research view'ları yalnızca `research.cohort_status`'ta `revealed` olan kohortları gösterir. Bu tabloya **yalnızca `status_relay`** yazabilir; Research kendi kapısını açamaz |
| Research'ün dolaylı erişimi | Canlı mumlar, `trading.*` ve açığa çıkmamış kohortlar Research'e kapalı. Research yalnızca `< C` verisini okur |
| Kullanıcının seçici davranması | Kullanıcı PnL'i görür, ama manifestoyu ve sonucu değiştiremez, kohortu silemez ya da geri çekemez. Her varyant yeni kohort ve yeni penceredir. Tüm kohortlar raporlanır |
| Saat manipülasyonu | Yalnızca makine yöneticisi yapabilir. Kabul edilmiş risk; kaynak dosyaların tarih başlıkları loglanır |

**Ağ ve yönetim katmanları (ADR-0013):** §1'deki dört Docker ağı; `holdout` veritabanına `CONNECT` yalnızca `eval_*` rollerinde; `pg_hba` ile yalnızca `holdout` ağından; host'a yalnızca loopback yayın ve bu port üzerinden `holdout` veritabanına ve superuser'a giriş yok. Aynı makinedeki yönetici erişimi bu katmanları aşabilir (kabul edilmiş artık risk). İzolasyon gizlilik sağlamaz; amaç iş akışı kaynaklı kontaminasyonu önlemektir.

### 6.3 Kohortlar ve manifesto
Kohort, birlikte dondurulan en fazla 3 strateji ve ortak protokoldür. Manifesto **değiştirilemez.**

**Yetkili kayıt ve zaman damgası:** Holdout DB'sindeki `results.cohort_manifests` tablosudur (append-only, sunucu saati). M5'ten önce, yani gerçek evaluator yokken (Kohort 0), yerel git commit'i manifestoyu sabitler. **Kohort 0 istisnası (U, 2026-10-14):** bu commit'in zaman damgası Kohort 0'ın `registered_at` değeri kabul edilir; `A_0` bundan aynı kuralla hesaplanır. M5'te evaluator veritabanına aktarılırken orijinal `registered_at` ve `A_0` korunur. Bu aktarım, istemci değerlerini reddeden normal kayıt yolu değil, audit'e yazılan bir migration/seed istisnasıdır; `CHECK (A_c > registered_at)` kuralını sağlar. Yerel commit zamanının yazar tarafından belirlendiği ve bağımsız bir zaman kanıtı olmadığı kabul edilmiş bir sınırlamadır.

**GitHub push'u isteğe bağlı ek kanıttır:** Her zaman kullanıcı onayına tabidir ve dondurmanın şartı değildir. Böylece otomatik dondurma, GitHub token'ı gerektirmez.

**Git'teki manifestolar** (`docs/cohorts/`) yalnızca eklenir. Force-push yapılmaz; düzenleme için izin kuralı onay ister.

**Manifesto alanları:**
- strateji tanımları ve hash'leri;
- parite kümesi ve kategorileri;
- zaman dilimi;
- veri kaynağı ve checksum politikası;
- engine image digest'i ve git SHA;
- benchmark;
- ücret ve slippage modeli;
- sizing, allocation ve risk modeli ile `RiskConfig` snapshot'ı;
- başlangıç sermayesi;
- birincil ve ikincil metrikler;
- protokol alanları (§6.5);
- `A_c`, `L_c`, veri kalitesi kuralları.

**Sınırlar (U3, U4):**
- aynı anda en fazla 2 açık kohort;
- kohort başına en fazla 3 strateji;
- aile başına ömür boyu en fazla 2 ileriye dönük deneme (ikinci denemede α/2);
- tanısal: **aile başına ömür boyu 1**, kesim dönemi başına toplam 10.

Hepsi `eval_registry` tarafından holdout DB'sinde uygulanır.

**Aile kimliği** (sistem hesaplar):
- DSL normalize edilir: kanonik JSON, değişme özelliği olan operandların sıralanması, totolojilerin sadeleştirilmesi, tekrarların kaldırılması.
- Parametreler, zaman dilimi, parite kümesi, model sürümleri, benchmark, metrikler ve engine çıkarıldıktan sonra kalan yapının hash'i aile kimliğidir. Bu alanlarda yapılan değişiklikler **aynı aile** sayılır ve kotadan düşer.
- Kural grafiğindeki değişiklik yeni aile oluşturur. **İstisna:** araştırma verisinde pozisyon korelasyonu ≥ 0,9 ya da işlem girişlerinin Jaccard örtüşmesi ≥ 0,8 ise aday mevcut aileye atanır (mühendislik başlangıç değerleri).
- **Benzerlik kontrolünün ölçeklenmesi (T21, ADR-0015):** aday tüm geçmiş deneylerle değil, yalnızca **aile temsilcileriyle** (her ailenin kayıt sırasına göre ilk üyesi) karşılaştırılır. Temsilci için yalnızca sıkıştırılmış artefaktlar saklanır: parite başına 1h ızgarada giriş olayı bitseti ve 1d ızgarada `int8` pozisyon vektörü. Artefaktlar parti halinde okunur; veri snapshot'ı değişince yeniden üretilir.
- Kullanıcı bir adayı elle mevcut bir aileye atayabilir, bir aileyi bölemez.
- **Sınırlama:** Ne hash ne de korelasyon tek başına atlatılamazlık garantisi verir. Asıl güvence global sınırlar ve tam raporlamadır ("n ileriye dönük denemeden k Validated"; yeni aile oluşturma sıklığı).

### 6.4 İki izli yaşam döngüsü (U1, ADR-0012)
**Araştırma:**
`Hypothesis → Defined → Backtested → RobustnessEvaluated → Candidate` (otomatik kapılar)

**Operasyonel iz:**
`Candidate → Frozen → PaperTrading → TestnetActive → Disabled | Retired | Rejected`

| Geçiş | Kapı | Yetki |
|---|---|---|
| `Candidate → Frozen` | Kapılar geçildi; tanısal sonuç (varsa) `Rejected` değil; manifesto yetkili kayda yazıldı (GitHub push şart değil); `eval_registry` sınırları kabul etti | Otomatik |
| `Frozen → PaperTrading` | Parity replay başarılı: L1 sinyal paritesi ve L2 karar paritesi birebir (ADR-0015). Mutlak emir büyüklükleri karşılaştırılmaz | **Otomatik (D3)** |
| `PaperTrading → TestnetActive` | Asgari paper süresi (TBD) ve sıfır kritik hata | **Kullanıcı onayı (D3)** |
| `* → Disabled / Retired` | Operasyonel güvenlik | Kullanıcı ya da risk motoru; her zaman serbest; audit'e yazılır |

**Doğrulama durumu** (ayrı eksen; bir view'dır ve sonuç tablosundan türetilir):
`Unvalidated → ProspectiveSealed → Validated | Failed | Inconclusive | Invalid`

- `Validated`, **yalnızca** `results.prospective_results` tablosundan türetilir. Bu tabloya yalnızca `eval_prospective` yazabilir. Tanısal tablo, `Validated` değerini alanının alabileceği değerler arasında hiç içermez. Registry, Ops API, Research ve `eval_diagnostic` bu durumu set edemez. Paper ve Testnet metrikleri hesaba girmez. `HoldoutEnvironmentVerified` yalnızca bu yazımı kapılar.
- **Registry'deki strateji sürümleri değiştirilemez** (`UPDATE` ve `DELETE` yetkisi yok). Trading.Host bir stratejiyi çalıştırmadan önce tanımın hash'ini manifesto hash'iyle karşılaştırır; uyuşmazlık varsa stratejiyi çalıştırmaz ve alarm üretir.
- **Testnet doğrulama gerektirmez.** `Validated` olmayan stratejiler "deneysel" etiketi taşır.
- **`Validated` gerektiren işler:** "doğrulanmış" etiketi, LLM Allocator'ın ağırlık vermesi (M8) ve kapsam dışı her türlü yüksek riskli terfi.
- **Operasyonel güvenlik eylemleri** her zaman serbesttir ve kohort değerlendirmesini **etkilemez:** devre dışı bırakma, kill switch'ler, yeni emirlerin engellenmesi, pozisyonların kapatılması. Değiştirilemezlik yalnızca manifestolar, sonuçlar ve audit geçmişi için geçerlidir.

### 6.5 Süre, protokol ve sonuç kuralları
**Süre `L_c`** (ön kayıtla, yalnızca R'den gelen girdilerle):
1. İşlem sıklığı `f` ve getiri belirsizliği (block bootstrap) hesaplanır.
2. Basit rejim etiketleri (BTC'nin 90 günlük getiri yönü × volatilite tertili) içinde en kötü rejimin değerleri kullanılır.
3. Simülasyonla, α_c düzeyinde hedef güç için gereken `n_days` ve `n_trades` bulunur. `L_req = max(n_days, n_trades / f)`.
4. `L_c = max(L_req, 6 ay)`, ay sınırına yuvarlanır. `L_req > 12 ay` ise kohort **gücü yetersiz** olarak işaretlenir ve en iyi ihtimalle `Inconclusive` olabilir.

6 ve 12 ay **varsayılandır, istatistiksel yeterlilik garantisi değildir.**

**Güç analizinin sınırları:** durağan olmayan piyasa; ileriye dönük rejimin bilinemezliği; seçim nedeniyle şişkin backtest etkisi; kalın kuyruklar ve az sayıda işlem. Güç analizi bir planlama aracıdır.

**Protokolün zorunlu alanları:**
- birincil metrik;
- benchmark;
- test ve yönü;
- **kohort içi α**;
- çoklu test düzeltmesi (kohort içinde Holm; aile içinde Bonferroni);
- **hedef güç**;
- **ekonomik minimum etki δ** (maliyetler düşülmüş yıllık excess getiri; taban değer ile küçültülmüş backtest etkisinden büyük olanı);
- maliyet ve slippage modeli;
- sizing ve risk modeli;
- **maksimum drawdown kuralı**;
- veri kalitesi kabulü;
- güç analizi artefaktları;
- ikincil metrikler (yalnızca betimleyici).

**Kalın** olanlar **bloke edici TBD**'dir (M3'te Protokol v1 ve kullanıcı incelemesi). Eksik protokolle dondurulan kohort "doğrulamaya uygun değil" olarak işaretlenir: paper ve Testnet'e girebilir, sonucu en iyi ihtimalle `Inconclusive (protocol incomplete)` olur. Eksik parametre sonradan eklenemez.

**Test ve güven aralığı (tek yöntem):**
- **H0:** birincil metrik (maliyetler düşülmüş excess getiri) ≤ 0. Test tek yönlüdür.
- Kullanılan α, **düzeltilmiş α**'dır: önce aile içi Bonferroni (ailenin ikinci denemesinde α/2), sonra kohort içinde Holm.
- Aynı block bootstrap'tan, düzeltilmiş α ile **tek yönlü `(1 − α)` güven sınırları** hesaplanır:
  - "düzeltilmiş p < α" ile "alt sınır > 0" eşdeğerdir;
  - Failed kuralındaki üst sınır da aynı düzeyde hesaplanır.

**δ'nın uygulanma biçimi (Protokol v1'de kullanıcı kararı, B5):**
- **(a)** nokta tahmini ≥ δ (varsayılan yazım);
- **(b)** alt sınır ≥ δ (daha sıkı; daha uzun pencere gerektirir).

Seçilen biçim protokolde ön kayıtlıdır.

**Karar sırası** (ilk sağlanan koşul geçerlidir):

| Sıra | Koşul | Sonuç |
|---|---|---|
| 1 | Kanıtlı teknik neden (aşağıda) | `Invalid` |
| 2 | Protokol eksik ya da kohort doğrulamaya uygun değil | `Inconclusive (protocol incomplete)` |
| 3 | Drawdown kuralı ihlal edildi (**işlem sayısı ve güçten bağımsız**) | `Failed` |
| 4 | Üst sınır < δ (güçten bağımsız; aralık ekonomik bir etkiyi dışlıyor) | `Failed` |
| 5 | Gücü yetersiz kohort ya da gerçekleşen işlem sayısı < plan | `Inconclusive` |
| 6 | Alt sınır > 0 **ve** δ koşulu ((a) ya da (b)) sağlandı | `Validated` |
| 7 | Diğer tüm durumlar | `Inconclusive` |

Veri kalitesi kabul edilmezse 1. adım uygulanır.

**İstatistiksel anlamlılık tek başına ekonomik başarı değildir.**

**`Invalid` ve yeniden değerlendirme:** `Invalid` yalnızca kanıtla verilir. Kabul edilen nedenler:
1. P penceresinde veri kalitesi hatası (kanıt: importçu raporu);
2. deterministik olmayan ya da çöken evaluator (kanıt: farklı sonuç hash'leri);
3. dondurulmuş image digest'i üzerinde, sonuçtan bağımsız bir regresyon testiyle gösterilen ve sonucu etkileyen engine hatası;
4. doğrulanmamış ortam.

Kötü performans ve strateji tanımındaki hatalar `Invalid` **sayılmaz.**

Yeniden değerlendirme yalnızca **aynı manifesto, aynı pencere, aynı kaynak ve aynı digest** ile yapılır; kullanıcı Ops API üzerinden gerekçeyle başlatır; 1. ve 4. nedenler için en fazla 2 kez. Engine hatasında kohort `Invalid (engine defect)` olarak kalır; düzeltilmiş engine ile açılan yeni kohort yeni bir penceredir ve aile kotasından düşer (aile ömrü boyunca bir kez audit'li istisna tanınır). Her koşu, Invalid olanlar dahil, append-only kaydedilir ve raporlanır.

**Tanısal holdout kullanımı:** Aile başına ömür boyu 1 değerlendirme, kesim dönemi başına toplam 10. Sonuç yalnızca `NotRejected` ya da `Rejected` olabilir. Research yalnızca bu değeri görür, metrikleri yalnızca kullanıcı görür. **Eleyebilir, terfi ettiremez.** `NotRejected` hiçbir geçişi açmaz. Sonuç kayıtlarında, hipotezi üreten modelin bilgi kesim tarihinin pencereyle çakışması işaretlenir (bilgi kontaminasyonu).

**Fark raporu (L3, ADR-0015):** Sonuç açığa çıktıktan sonra, evaluator simülasyonu ile paper ve Testnet sonuçları **normalize değerlerle** karşılaştırılır: birim sinyaller, işlem zamanlaması eşleşme oranı, işlem başına yüzde getiri, maruziyete göre normalize getiri, slippage (bps). Evaluator sabit başlangıç sermayesiyle izole simüle ederken paper ortak ve değişen sermaye kullandığı için **mutlak emir büyüklükleri karşılaştırılmaz.** Farklar kategorize edilir: portföy kısıtları (ortak sermaye, risk kırpması, maksimum pozisyon), `MIN_NOTIONAL` atlamaları, dolum ve slippage, canlı ve arşiv mum farkları. Rapor yalnızca bilgilendiricidir ve doğrulamaya girmez. Model düzeltmeleri yalnızca gelecek kohortlara uygulanır.

### 6.6 Araştırma kapıları
Eşikler TBD; M3'te kalibre edilir. Kapılar:
- maliyetler düşülmüş getiri;
- minimum işlem sayısı ve bootstrap güven aralığı;
- benchmark karşılaştırması;
- walk-forward tutarlılığı;
- parametre komşuluğu;
- çoklu varlık;
- deneme sayısıyla düzeltilmiş Deflated Sharpe.

Deneme sayacı, menşeinden bağımsız olarak (LLM, grid, insan, `dev-assisted`) tüm denemeleri sayar.

## 7. Domain varlıkları (özet)
- **Veri:** `TradingPair`, `PairCategory` (Core / E1 / E2, `first_open`, `U0`), `Candle`, `LiveCandle`, `DatasetSnapshot`.
- **Holdout ve doğrulama:** `Cohort`, `CohortManifest` (hash'li, değiştirilemez; `registered_at` ve `A_c` sunucu tarafından atanır), `ProtocolVersion`, `DiagnosticResult` (`NotRejected` / `Rejected`), `ProspectiveRun`, `ProspectiveResult`, `ValidationStatus` (view; yalnızca `ProspectiveResult`'tan türetilir), `CohortStatus` (ops DB; `status_relay` yazar), `EvaluatorRelease`.
- **Araştırma:** `IndicatorSpec`, `StrategyDefinition`, `TrialFamily`, `Hypothesis` (menşei: `llm` / `grid` / `human` / `dev-assisted`; bilgi kesim tarihi), `Experiment`, `ExperimentResult`, `StrategyLifecycleRecord`.
- **Operasyon:** `Signal`, `TargetAllocation`, `AllocationProposal`, `RiskDecision`, `OrderIntent`, `Order`, `Fill`, `Position`, `LedgerEntry`, `BalanceSnapshot`, `ReconciliationRun`.
- **AI:** `TaskQualification`, `QualificationProtocol` (hash'li), `DecisionTrace`, `CostLedgerEntry`, `AiKillSwitchState`.
- **Denetim:** `RiskConfig` (`ExperimentalPairsMaxExposure` dahil), `KillSwitchState`, `AuditEvent`.

## 8. Kontrollü web araştırması (M7, ADR-0008)
- SourceGateway deterministiktir: allowlist (redirect'ler dahil), robots.txt, domain başına rate limit, önbellek (URL ve içerik hash'i; varsayılan TTL 30 gün), temizleme ve `SourceDocument` kaydı (URL, zaman, hash, güven seviyesi).
- Extractor **tool'suz** çalışır ve yalnızca FindingExtraction için nitelikli bir model varsa devreye girer. Çıktısı `Finding[]` şemasıyla sınırlıdır. Alıntılar, kendi çıkardığımız metinde deterministik olarak aranarak doğrulanır.
- Researcher ham metni görmez. Bulgular yalnızca hipotez üretir; hattın hiçbir adımı atlanamaz.
- **Google Search grounding ve URL context otonom hatta kullanılmaz (T13).** Grounding şartları (görüntüleme, saklama, "learn from" yasağı; S-G5) kalıcı otonom hafızayla bağdaşmıyor. URL context, getirmeyi Google'a bıraktığı için redirect allowlist'ini, içerik hash'ini ve alıntı doğrulamasını devre dışı bırakır; aynı veriyi kendi getiricimiz zaten sağlıyor. Etkileşimli grounding modu: P2.
- Kaynak güven seviyeleri (T1: arXiv q-fin, resmi dokümantasyon; T2: kullanıcının tek tek onayladığı kaynaklar) yalnızca önceliği belirler; kanıt değeri taşımaz. Allowlist'i yalnızca kullanıcı değiştirir.

## 9. Hata yönetimi
Bkz. `risk-model.md` §4.

## 10. Güvenlik
- Exchange anahtarı (Testnet): trade izni, withdrawal yok. IP kısıtı yerine kodda yalnızca `testnet.binance.vision` host allowlist'i ve redirect reddi uygulanır (D33, ADR-0021). Anahtar yalnızca Trading.Host'un Docker secret'larında bulunur.
- **Secret'lar:**
  - Geliştirmede `dotnet user-secrets` (yalnızca geliştirme anahtarları; holdout secret'ı yok).
  - Runtime'da Docker secret dosyaları (yerel diskte; Claude Code için `Read(**/secrets/**)` deny kuralı).
  - Holdout veritabanı parolası yalnızca evaluator secret'ında; `status_relay` kimliği de yalnızca evaluator'da (D24).
  - Research.Host'ta yalnızca submit-only evaluator token'ı.
  - Tüm runtime anahtarları Claude Code ile aynı makinededir: kabul edilmiş artık risk (ADR-0013).
- **Ağ:** Host'a yalnızca `127.0.0.1` üzerinden port yayınlanır (geliştirme veritabanı, Ops API, dashboard). `eval` ve `holdout` ağları `internal` (§1). SSH yok.
- **Claude Code korkulukları (T17, ADR-0013 §6):**
  - Proje talimat dosyası: 2026-01-01 ve sonrasına ait piyasa verisi Claude Code tarafından indirilmez, okunmaz ve incelenmez; holdout veritabanına ve evaluator container'ına bağlanılmaz; holdout korumaları ve evaluator kodu, açık kullanıcı isteği olmadan değiştirilmez.
  - Proje izin kuralları: `docker exec`, `docker compose exec`, `docker cp`, SSH ve arşiv host'una doğrudan erişim için deny kuralları.

  Yerel topolojide önemleri artar, ama bunlar teknik bir güven sınırı değildir; teknik katmanlar §6.2'deki rol, `CONNECT`, `pg_hba` ve ağ kontrolleridir.
- Log redaction ve kanarya testleri.
- Audit tablosunda uygulama rolleri yalnızca `INSERT` yetkisine sahiptir (`UPDATE`/`DELETE` yok).

## 11. Gözlemlenebilirlik (T15)
- Yapılandırılmış JSON loglar (`Microsoft.Extensions.Logging` ve JSON console formatter). Her satırda `CycleId`, `JobId`, `TaskId`, `OrderId` gibi correlation ID'leri.
- Health endpoint'leri (`/health/live`, `/health/ready`). Hazırlık; DB, son mumun tazeliği ve kill switch durumlarını kapsar.
- Metrikler ops DB'de sorgulanabilir tablolar olarak tutulur (döngü süreleri, gecikme, maliyet). Prometheus/Grafana/ELK yok; ihtiyaç doğarsa M10'da yeniden değerlendirilir.
- Alarmlar M10'da.

## 12. Dağıtım (ADR-0013)
- **Tek yerel makine** (D5 rev.): Docker Compose ve Testcontainers. VPS yok.
- **Kaynak sınırları:** her servis için `mem_limit` ve `cpus`. Başlangıç değerleri: PostgreSQL 1 GB / 1.0, Trading.Host 384 MB / 0.5, Research.Host 1.5 GB / 2.0, HoldoutEvaluator 1.5 GB / 2.0 (M3 ve M5'te ölçülüp ayarlanır). Docker Desktop (WSL2) toplam bellek tavanı kullanıcı makine ayarıdır.
- **Çalışma biçimi:** Trading.Host ve PostgreSQL sürekli; Research.Host ve HoldoutEvaluator iş bazlı (boşta bellek tutmaz). Ağır işler aynı anda çalıştırılmaz.
- **Deploy (T19):** kullanıcı yerelde etkileşimli olarak `docker compose build` / `up` başlatır; evaluator image digest'i `EvaluatorRelease` olarak kaydedilir. Registry ve SSH yok.
- **Yedek:** `ops` veritabanı için günlük, şifreli `pg_dump` ve makine dışında bir kopya. `holdout` veritabanı yeniden üretilebilir olduğu için (kaynaktan ve checksum'la) yedeklenmez; yalnızca değerlendirme kayıtları ayrıca yedeklenir.
- **Uptime:** uyku, yeniden başlatma ve güncellemeler paper/Testnet sürekliliğini keser (kabul edilmiş artık risk, `risk-model.md` §4).

## 13. Reddedilen alternatifler
| Alternatif | Neden |
|---|---|
| Mikroservisler, broker, Redis, TimescaleDB | Gereksiz (ADR-0002) |
| VPS (holdout ve sürekli çalışma için) | Kullanıcı kararıyla iptal (D5 rev., ADR-0013). Önceki tasarım: ADR-0009 |
| İki ayrı PostgreSQL instance'ı | 16 GB RAM'de ek bellek; ayrı veritabanı, `CONNECT` ve `pg_hba` aynı makinede benzer pratik ayrımı sağlar (ADR-0013) |
| Ayrı StatusRelay container'ı | Ek bellek ve işletim yükü (D24) |
| Holdout'u ayrı bir OS kullanıcısında tutmak | Windows'ta yönetici veya Docker erişimi olan biri ya da bir araç sınırı aşabilir (ADR-0009) |
| Holdout değerlendirmesini CI'da yapmak | Workflow geliştirme ortamından değiştirilebilir; uzak repo gerektirir (ADR-0009) |
| Harici image registry | Ek hesap ve kimlik bilgisi; tek makine için gereksiz |
| Paper'da tick ya da 1 dakikalık veriyle mum içi stop izleme | Backtest ile parity bozulur; ek veri ve bellek (ADR-0014) |
| Benzerlik kontrolünde tüm geçmiş deneylerle karşılaştırma | Bellek ve işlem darboğazı (ADR-0015) |
| Tarihsel holdout'u birincil doğrulama kanıtı saymak | Sonuçlar insanlar ve LLM'ler tarafından biliniyor olabilir. Yalnızca eleme amaçlı kullanılır (ADR-0006) |
| Testnet'i doğrulamaya bağlamak (6–12 ay bekleme) | Güvenli deneme gereksiz yere gecikirdi. Bunun yerine iki izli yaşam döngüsü (ADR-0012) |
| Sağlayıcılar arası LLM fallback'i | Kalite eşdeğerliği varsayımı (D20) |
| LLM'in kod üretmesi, mum başına LLM kararı | ADR-0003, ADR-0005 |
| Grounding ve URL context ile otonom araştırma | §8 |
| Prometheus/Grafana/ELK | Tek kullanıcı için gereksiz işletim yükü |

## 14. Uygulama ilkeleri (T23, T24)
- **YAGNI ve D15 odağı:** İlk dikey dilim M0–M2'dir. M1 ve M2 kodlanırken M3+ bileşenleri (DSL, kohort, evaluator, LLM, `ops`/`holdout` veritabanları) koda erken eklenmez; arayüz, tablo ya da yapılandırma "ileride lazım olur" diye açılmaz. Gerekli olduğu milestone'da eklenir.
- **Model adları konfigürasyondan:** `gemini-3.5-flash-lite` gibi model adları kodda sabit yazılmaz. Sağlayıcı, model, prompt sürümü ve `qualificationId` görev politikası konfigürasyonundan okunur (§3.1). Testler sahte sağlayıcıyla ve konfigürasyondan verilen adlarla çalışır.
