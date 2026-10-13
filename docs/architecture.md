# Mimari

Son güncelleme: 2026-10-13 (rev. 4) · ADR'ler: [adr/](adr/) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

> Durum: Bu belgede anlatılan her şey **planlanmıştır, uygulanmamıştır**. Karar etiketleri (U/T/P/TBD) için bkz. `requirements.md`.

## 1. Ortamlar ve topoloji (T1, ADR-0009)

Kod tarafında modüler bir monolit (tek solution, ADR-0001). Çalışma zamanında iki ortam var:

```
GELİŞTİRME MAKİNESİ (Windows, Claude Code burada çalışır)
  - Kaynak kod, testler, Testcontainers
  - dev PostgreSQL: yalnızca araştırma kesim tarihinden ÖNCEKİ veri (< 2026-01-01T00:00Z; DB CHECK + importçu koruması)
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
| dev DB | Geliştirme makinesi | `market.candles` (yalnızca kesim tarihinden önce), deneyler (geliştirme amaçlı) | Geliştirici, testler, Claude Code |
| ops DB | VPS | `market.candles` (araştırma verisi, kesimden önce), `market.live_candles` (canlı veri; yalnızca Trading), deneyler, kayıt, emirler, ledger, audit, trace | Modül başına DB rolleri (§6.2). Research yalnızca `research.*` view'larını okur |
| holdout DB | VPS, **ayrı bir PostgreSQL instance'ı** | Tanısal ve ileriye dönük pencere verisi (evaluator'ın kendi import'u), manifestolar ve sonuçlar | Yalnızca evaluator rolleri (`eval_diagnostic`, `eval_prospective`) |

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

Hiçbir LLM rolü dondurma, holdout değerlendirmesi ya da doğrulama durumu üzerinde yetkiye sahip değildir. Kohort işlemlerini deterministik Registry ve evaluator yürütür (§6).

## 5. Operasyonel döngü (her saat)
1. MarketData kapanan mumu çeker ve doğrular. Veri bayatsa yalnızca çıkışlar çalışır.
2. Reconciliation çalışır. Tutarsızlık varsa yeni giriş açılmaz.
3. StrategyEngine sinyalleri üretir.
4. **Varsayılan allocation politikası (T11)** hedef ağırlıkları hesaplar. M8'den sonra, nitelikli bir model varsa Allocator bu ağırlıkları bant içinde ayarlayabilir; **ama yalnızca `Validated` stratejiler için.** Her durumda sonucu deterministik Sizer kullanır.
5. Risk her intent için onay, kırpma ya da ret kararı verir. Deneysel paritelerin (E1/E2) toplam maruziyeti `ExperimentalPairsMaxExposure` (%10) tavanına tabidir (`risk-model.md` §2). Risk motoru doğrulama durumundan ve holdout yaşam döngüsünden **bağımsızdır.**
6. Execution önce `clientOrderId`'yi kaydeder (outbox), sonra emri gönderir.
7. Dolumlar işlenir, ledger ve audit güncellenir.

```
Intent → RiskApproved | RiskRejected
RiskApproved → Submitting → Accepted → PartiallyFilled → Filled | Canceled | Expired | ExchangeRejected
Submitting --timeout/5xx--> Unknown --(origClientOrderId ile sorgu)--> gerçek durum
```

## 6. Araştırma, değerlendirme ve holdout (ADR-0006, ADR-0009, ADR-0012)

### 6.1 Zaman semantiği ve veri dönemleri
- **Zaman:** Tüm zaman damgaları UTC anlarıdır (DB'de `timestamptz`, kodda offset 0 olan `DateTimeOffset`). DB oturum saat dilimi `UTC`'dir ve yerel saat dönüşümü yapılmaz. Aralıklar yarı açıktır: `[başlangıç, bitiş)`.
- **Mum üyeliği:** Açılışı `o`, aralığı `Δ` olan bir mum `[S, E)` dönemine ancak `S ≤ o` **ve** `o + Δ ≤ E` ise aittir; yani mumun tamamı dönemin içinde olmalıdır. Örneğin 2025-12-29 açılışlı haftalık mum 2026-01-04'te kapandığı için araştırma dönemine dahil değildir.
- **Araştırma kesim tarihi** `C = 2026-01-01T00:00:00Z` (sabit). İlerletilmesi yalnızca kullanıcı kararıyla mümkündür; en son **kapanmış** ileriye dönük pencerenin bitişini aşamaz ve açık bir kohortun penceresine giremez.

| Dönem | Aralık | Erişen | Niteliği |
|---|---|---|---|
| Araştırma (R) | `[2022-01-01, C)` (çekirdek); E1 için `[U0, C)` (ADR-0012) | Research, backtest, LLM rolleri | Tek araştırma veri seti |
| Tanısal holdout (D_c) | `[C, A_c)` | Yalnızca `eval_diagnostic` | **Kör değil** (bilgi kontaminasyonu). Yalnızca **eler**, terfi ettirmez |
| İleriye dönük birincil holdout (P_c) | `[A_c, A_c + L_c)` | Yalnızca `eval_prospective`, pencere kapandıktan sonra | Veri dondurmadan sonra oluşur. **Doğrulamanın tek kaynağıdır** |

`A_c` = kohortun dondurulduğu `F_c` anı + 24 saatten sonraki ilk UTC gece yarısı. `L_c` §6.5'e göre belirlenir.

### 6.2 Saklama, erişim, değerlendirme ve açıklama
**Saklama.** İleriye dönük pencerenin verisi, pencere bitmeden de **ops DB'sinde canlı veri olarak bulunur.** Koruma verinin yokluğuna değil, erişim kontrolüne dayanır.

| Tablo / depo | İçerik | Yazan |
|---|---|---|
| `market.candles` (dev ve ops) | Arşivden import edilen araştırma verisi. **Her ortamda** `CHECK (open_time + interval ≤ C)` | `market_import` |
| `market.live_candles` (ops) | Canlı mumlar (kesim sonrası) | Yalnızca `trading_app` |
| `trading.*` (ops) | Paper ve Testnet emirleri, dolumlar, PnL | `trading_app` |
| holdout DB `diagnostic.*` | Tanısal pencere verisi (evaluator'ın kendi import'u) | `eval_diagnostic` |
| holdout DB `prospective.*` | P_c verisi; **yalnızca pencere kapandıktan sonra** import edilir | `eval_prospective` |
| holdout DB `results.*` | Manifestolar, koşular ve sonuçlar (append-only) | Evaluator rolleri |

**Evaluator'ın veri edinimi.** Evaluator ops DB'sine erişemez ve canlı veriyi kullanmaz. Pencere verisini, araştırma verisiyle aynı kanonik kaynaktan (Binance açık veri arşivi, checksum doğrulamalı) kendisi import eder. Bunu `A_c + L_c` artı 3 günlük yayın tamponundan (başlangıç değeri) sonra yapar. Isınma verisi de aynı yoldan gelir.

**Kimlikler ve izinler:**

| Kimlik | Okuyabilir | Okuyamaz |
|---|---|---|
| `research_app` | `research.candles`, deneyler, `research.revealed_cohorts` (yalnızca açığa çıkmış, yuvarlanmış özet), `research.revealed_paper_summary` (yalnızca açığa çıkmış kohortlar) | `market.live_candles`, `trading.*`, holdout DB'si |
| `trading_app` | `market.live_candles`, `trading.*`, aktif strateji tanımları | holdout DB'si, araştırma tabloları |
| `ops_api` (kullanıcı) | Paper ve Testnet izlemesi (açık kohortlar dahil), kohort durumları, açığa çıkmış tam raporlar | holdout DB'sinin ham verisi |
| `eval_diagnostic` | `diagnostic.*`, `results.*` (yalnızca ekleme) | `prospective.*`, ops DB'si |
| `eval_prospective` | `prospective.*`, `results.*` (yalnızca ekleme) | `diagnostic.*`, ops DB'si |
| VPS root (insan) | Teknik olarak her şey | Kabul edilmiş artık risk; süreç kuralı ve audit |

**Tehditler ve engeller (en az iki katman):**

| Tehdit | Engel |
|---|---|
| Erken değerlendirme | Evaluator zamanlayıcısı, enjekte edilebilir saatle `now < A_c + L_c + tampon` iken çalışmaz. "Şimdi değerlendir" API'si yoktur. DB trigger'ı, penceresi kapanmamış bir kohort için `prospective.*` ve sonuç tablolarına `INSERT`'i reddeder |
| Yetkisiz import | Importçu yalnızca kayıtlı kohort pencerelerini import eder; serbest tarih aralığı kabul etmez. Aynı DB trigger'ı ve host allowlist'i |
| Erken açıklama | Sonuç pencere bitmeden hesaplanamaz. Research view'ları yalnızca `revealed` kohortları gösterir |
| Research'ün dolaylı erişimi | Canlı mumlar, `trading.*` ve açığa çıkmamış kohortlar Research'e kapalı. Research yalnızca `< C` verisini okur |
| Kullanıcının seçici davranması | Kullanıcı PnL'i görür, ama manifestoyu ve sonucu değiştiremez, kohortu silemez ya da geri çekemez. Her varyant yeni kohort ve yeni penceredir. Tüm kohortlar raporlanır |
| Saat manipülasyonu | Yalnızca root yapabilir. Kabul edilmiş risk; kaynak dosyaların tarih başlıkları loglanır |

**Ağ ve yönetim katmanları:** §1'deki dört ağ; hiçbir port yayınlanmaz; geliştirme makinesinin VPS'e etkileşimsiz erişimi yoktur (T19). İzolasyon gizlilik sağlamaz; amaç iş akışı kaynaklı kontaminasyonu önlemektir.

### 6.3 Kohortlar ve manifesto
Kohort, birlikte dondurulan en fazla 3 strateji ve ortak protokoldür. Manifesto **değiştirilemez** ve git'e commit edilip GitHub'a push edilerek zaman damgası alır. M5'ten itibaren evaluator'a da kaydedilir.

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
- tanısal: aile başına 1, kesim dönemi başına toplam 10.

**Aile kimliği** (sistem hesaplar):
- DSL normalize edilir: kanonik JSON, değişme özelliği olan operandların sıralanması, totolojilerin sadeleştirilmesi, tekrarların kaldırılması.
- Parametreler, zaman dilimi, parite kümesi, model sürümleri, benchmark, metrikler ve engine çıkarıldıktan sonra kalan yapının hash'i aile kimliğidir. Bu alanlarda yapılan değişiklikler **aynı aile** sayılır ve kotadan düşer.
- Kural grafiğindeki değişiklik yeni aile oluşturur. **İstisna:** araştırma verisinde pozisyon korelasyonu ≥ 0,9 ya da işlem girişlerinin Jaccard örtüşmesi ≥ 0,8 ise aday mevcut aileye atanır (mühendislik başlangıç değerleri).
- Kullanıcı bir adayı elle mevcut bir aileye atayabilir, bir aileyi bölemez.
- **Sınırlama:** Ne hash ne de korelasyon tek başına atlatılamazlık garantisi verir. Asıl güvence global sınırlar ve tam raporlamadır ("n ileriye dönük denemeden k Validated"; yeni aile oluşturma sıklığı).

### 6.4 İki izli yaşam döngüsü (U1, ADR-0012)
**Araştırma:**
`Hypothesis → Defined → Backtested → RobustnessEvaluated → Candidate` (otomatik kapılar)

**Operasyonel iz:**
`Candidate → Frozen → PaperTrading → TestnetActive → Disabled | Retired | Rejected`

| Geçiş | Kapı | Yetki |
|---|---|---|
| `Candidate → Frozen` | Kapılar geçildi; tanısal sonuç (varsa) "eleme" değil; manifesto kaydedildi; açık kohort sınırı aşılmadı | Otomatik |
| `Frozen → PaperTrading` | Parity replay başarılı | **Otomatik (D3)** |
| `PaperTrading → TestnetActive` | Asgari paper süresi (TBD) ve sıfır kritik hata | **Kullanıcı onayı (D3)** |
| `* → Disabled / Retired` | Operasyonel güvenlik | Kullanıcı ya da risk motoru; her zaman serbest; audit'e yazılır |

**Doğrulama durumu** (ayrı eksen; bir view'dır ve sonuç tablosundan türetilir):
`Unvalidated → ProspectiveSealed → Validated | Failed | Inconclusive | Invalid`

- `Validated`, **yalnızca** `eval_prospective` tarafından yazılan bir sonuç kaydından türetilir. Registry, Ops API ve Research bu durumu set edemez. Paper ve Testnet metrikleri hesaba girmez. `HoldoutEnvironmentVerified` yalnızca bu yazımı kapılar.
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

| Sonuç | Koşul |
|---|---|
| Validated | Protokol eksiksiz; veri kalitesi kabul edildi; gerçekleşen işlem sayısı ≥ plan; düzeltilmiş p < α; nokta tahmini ≥ δ; drawdown kuralı sağlandı |
| Failed | Protokol eksiksiz; veri kalitesi kabul edildi; işlem sayısı yeterli; ve (güven aralığının üst sınırı < δ **ya da** drawdown kuralı ihlal edildi) |
| Inconclusive | Diğer tüm durumlar (gücü yetersiz; yetersiz işlem; güven aralığı δ'yı içeriyor; protokol eksik) |
| Invalid | Yalnızca teknik nedenler (aşağıda) |

**İstatistiksel anlamlılık tek başına ekonomik başarı değildir.**

**`Invalid` ve yeniden değerlendirme:** `Invalid` yalnızca kanıtla verilir. Kabul edilen nedenler:
1. P penceresinde veri kalitesi hatası (kanıt: importçu raporu);
2. deterministik olmayan ya da çöken evaluator (kanıt: farklı sonuç hash'leri);
3. dondurulmuş image digest'i üzerinde, sonuçtan bağımsız bir regresyon testiyle gösterilen ve sonucu etkileyen engine hatası;
4. doğrulanmamış ortam.

Kötü performans ve strateji tanımındaki hatalar `Invalid` **sayılmaz.**

Yeniden değerlendirme yalnızca **aynı manifesto, aynı pencere, aynı kaynak ve aynı digest** ile yapılır; kullanıcı Ops API üzerinden gerekçeyle başlatır; 1. ve 4. nedenler için en fazla 2 kez. Engine hatasında kohort `Invalid (engine defect)` olarak kalır; düzeltilmiş engine ile açılan yeni kohort yeni bir penceredir ve aile kotasından düşer (aile ömrü boyunca bir kez audit'li istisna tanınır). Her koşu, Invalid olanlar dahil, append-only kaydedilir ve raporlanır.

**Tanısal holdout kullanımı:** Aile başına 1 değerlendirme, dönem başına toplam 10. Research yalnızca geçti/elendi bilgisini görür, metrikleri yalnızca kullanıcı görür. **Eleyebilir, terfi ettiremez.** Sonuç kayıtlarında, hipotezi üreten modelin bilgi kesim tarihinin pencereyle çakışması işaretlenir (bilgi kontaminasyonu).

**Fark raporu:** Sonuç açığa çıktıktan sonra, evaluator simülasyonu ile paper ve Testnet sonuçları karşılaştırılır (getiri farkı, işlem eşleşmesi, slippage). Rapor yalnızca bilgilendiricidir ve doğrulamaya girmez. Model düzeltmeleri yalnızca gelecek kohortlara uygulanır.

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
- **Holdout ve doğrulama:** `Cohort`, `CohortManifest` (hash'li, değiştirilemez), `ProtocolVersion`, `DiagnosticResult`, `ProspectiveRun`, `ProspectiveResult`, `ValidationStatus` (view), `EvaluatorRelease`.
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
- Exchange anahtarı: trade izni, withdrawal yok, IP kısıtı (VPS IP'si). Yalnızca Trading.Host'ta bulunur.
- **Secret'lar:**
  - Geliştirmede `dotnet user-secrets` (yalnızca geliştirme anahtarları; holdout ve VPS secret'ı yok).
  - VPS'te Docker secret dosyaları (root'a ait, 0400).
  - Holdout DB parolası yalnızca evaluator secret'ında.
  - Research.Host'ta yalnızca submit-only evaluator token'ı.
- **Ağ:** VPS'te dışa açık tek port SSH (parola girişi kapalı, anahtarlı). Ops API ve dashboard'a SSH tüneliyle erişilir. `eval` ve `holdout` ağları `internal` (§1).
- **Claude Code korkulukları (T17):**
  - Proje talimat dosyası: 2026-01-01 ve sonrasına ait piyasa verisi araştırma amacıyla indirilmez ve incelenmez; holdout korumaları ve evaluator kodu, açık kullanıcı isteği olmadan değiştirilmez.
  - Proje izin kuralları: VPS'e SSH ve arşiv host'una doğrudan erişim için deny kuralları.

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
| Tarihsel holdout'u birincil doğrulama kanıtı saymak | Sonuçlar insanlar ve LLM'ler tarafından biliniyor olabilir. Yalnızca eleme amaçlı kullanılır (ADR-0006) |
| Testnet'i doğrulamaya bağlamak (6–12 ay bekleme) | Güvenli deneme gereksiz yere gecikirdi. Bunun yerine iki izli yaşam döngüsü (ADR-0012) |
| Sağlayıcılar arası LLM fallback'i | Kalite eşdeğerliği varsayımı (D20) |
| LLM'in kod üretmesi, mum başına LLM kararı | ADR-0003, ADR-0005 |
| Grounding ve URL context ile otonom araştırma | §8 |
| Prometheus/Grafana/ELK | Tek kullanıcı için gereksiz işletim yükü |
