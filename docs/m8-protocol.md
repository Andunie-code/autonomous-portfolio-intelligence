# M8 Protokolü — Portfolio Allocator, gölge mod ve AllocationProposal yeterliliği (v1.1)

- Durum: **Ön kayıt — kullanıcı tarafından onaylandı (Kapı A, 2026-10-29, U; §0.2 A-1…A-7 dahil).** Ön kayıt kanıtı, bu belgenin onaylandığı git commit'idir. O commit'ten önce M8 kodu yazılmaz.
- **Pilot ve yeterlilik bloklarının tamamlanması:** prompt hash'i ve şema hash'i yalnızca kodla üretilebilir. Bu değerler Kapı B'nin sonunda §8'deki bloklara **v1.1** olarak eklenir. v1.1, §5–§7'deki kuralları değiştiremez; yalnızca hash ekler.
- **v1.1 (2026-10-29, Kapı B sonu; kullanıcı tarafından onaylandı 2026-10-30, U; §11 sapmaları dahil):** §8'e prompt ve şema hash'leri eklendi; pilot protokolü ayrı belge oldu ([`m8-alloc-pilot-protocol.md`](m8-alloc-pilot-protocol.md)); Kapı B uygulama notları §11'de. §5–§7'deki kurallar değişmedi. Pilot, onaylanan belge commit'lendikten sonra kaydedilir.
- Karar türleri: **U** = kullanıcı, **T** = teknik lider, **P** = öneri (kullanıcı onayı gerekir).
- İlgili: ADR-0005, ADR-0010, ADR-0011, ADR-0012, ADR-0014, ADR-0015, ADR-0017, ADR-0018, ADR-0020, ADR-0021, **ADR-0023**; `architecture.md` §2–§5; `testing-strategy.md` §6.1–§6.2; `risk-model.md` §1–§4; `sources.md` S-G1, S-G2; D1, D4, D10, D12, D16, D20, D29, **D34**; T7–T11, T17, T24.
- **Değiştirilmeyenler:**
  - T11 politikası (`allocation v1`), `RiskConfig` ve onaylı limitler (%20, %80, 5 pozisyon, %10 deneysel, D29 %60, D25 %10 hard stop);
  - kohort manifestoları; M2'nin dört sonuç hash'i; 36 engine regresyon hash'i; Kohort 0 ve 1 parity L1/L2 hash'leri;
  - D4 tavanları (`monthlyCapUsd` 10, `callCapUsd` 0,10, yeterlilik iş payı 0,30) ve LLM acil durdurma tetikleri;
  - HypothesisGeneration ve FindingExtraction kayıtları (`TaskQualification` 1 ve 2);
  - evaluator ve adında `Holdout` geçen dosyalar (M8'de değişiklik listesi **boştur**).

## 0. Kullanıcı kararları

### 0.1 Alınmış kararlar (U, 2026-10-29; D34)
| # | Karar |
|---|---|
| U-M8-1 | M8 başlatıldı. Kapılar: **A** = bu protokol, ADR-0023 ve belge güncellemeleri; **B** = çevrimdışı kod ve testler, `verify.ps1` yeşil, API maliyeti 0; **C** = kullanıcının koşuları (§9) |
| U-M8-2 | **Gölge kapsam:** gölge öneriler **tüm** `PaperTrading`/`TestnetActive` stratejilerinin paper run'ları için üretilir. Aktif modda ağırlık yalnızca `Validated` stratejilere uygulanır (§4.3) |
| U-M8-3 | **Bant:** `w' = m × w_T11`, m ∈ [0,5; 1,5]; ya da veto (giriş açılmaz). Bant dışı çarpan bant sınırına clamp edilir. Onaylı tavanlar bandın ardından ayrıca ve değişmeden uygulanır (`allocator-band v1`) |
| U-M8-4 | **Kapanış:** kabul edilen sapma. M8; kod, testler, gölge modun canlı başlatılması, pilot ve yeterlilik protokolünün kaydıyla kapanır. Yeterlilik penceresi ve sonucu arka planda sürer ve kabul raporuna ek olarak işlenir (M5/M6 deseni) |
| U-M8-5 | **Model ve bütçe:** `gemini-3.5-flash-lite` (S-G1, S-G2; 2026-10-29'da yeniden doğrulandı). Pilot işi **1,00 USD**, yeterlilik işi **3,00 USD**. Aylık 10 USD tavanı ve tüm otomatik tetikler aynen geçerlidir |

### 0.2 Kapı A değerleri (U, 2026-10-29)
| # | Konu | Değer | Bölüm |
|---|---|---|---|
| A-1 | Non-inferiority marjı | Ortalama günlük net getiri farkı için **yıllık −%3** (günlük `−0,03 / 365`) | §6.2 |
| A-2 | Drawdown koşulu | Her paper run'da `MDD_gölge − MDD_T11 ≤ 0` (kötüleşme yok; tolerans 0) | §6.2 |
| A-3 | Geçerli öneri oranı | Çağrıların (kill switch ve bütçe dışı) en az **%80**'i geçerli öneriyle bitmeli | §6.2 |
| A-4 | Süre kuralı | `N = max(28, ⌈(2,487 · σ̂_d / m)²⌉)` gün; `N > 56` ise pencere 56 gün ve sonuç en iyi ihtimalle `Inconclusive` | §6.3 |
| A-5 | Asgari karar | Pencerede en az **10** geçerli öneri; aksi halde `Inconclusive` | §6.2 |
| A-6 | Çağrı timeout'u | Allocator çağrısı için **30 sn** (görev politikasındaki 120 sn yerine) | §5.4 |
| A-7 | Pilot süresi | **14 gün** | §6.1 |

## 1. Kapsam ve sınırlar
- **Kapsam:**
  - Kernel: iki fazlı adım (`BeginStep` / `CompleteStep`), `StepContext.Allocation`, `allocator-band v1`, yeni L2 olayları;
  - yeni `Allocator` modülü: girdi paketi, prompt, şema, çıktı doğrulayıcı, clamp, kapı, advisor, non-inferiority değerlendiricisi;
  - Execution: `IAllocationAdvisor` portu, gölge motor, aktif mod entegrasyonu, mod okuma;
  - Ai: `JobKind.Allocation`, `QualificationDesign.NonInferiority`;
  - migration 0011; Ops API `GET /allocator`, `POST /allocator/mode`; dashboard "Allocator" sekmesi;
  - AiCli `alloc-spike`, `alloc-register`, `alloc-report`, `alloc-qualify`; `scripts/m8-*.ps1` (yalnızca kullanıcı).
- **Kapsam dışı:**
  - T11 politikasının ya da `RiskConfig`'in değiştirilmesi; rebalance (allocator yalnızca **yeni girişleri** etkiler);
  - Allocator'ın çıkış, stop, kill switch, devre dışı bırakma ya da kapatma üzerinde herhangi bir etkisi;
  - araştırma tarafında allocator kullanımı; allocator çıktısının hipotez ya da araştırma girdisi olması;
  - sağlayıcılar arası fallback; Anthropic runtime (D19).
- **Gerçeklik notu:** bugün `Validated` strateji yoktur (en erken Kohort 0, 2027; Kohort 1 hiç olamaz). Aktif mod kodlanır ve test edilir, ama gerçekte `Validated` bir strateji olana kadar hiçbir emri etkilemez.

## 2. Mimari ve yerleşim (ADR-0023)
- `PortfolioIntelligence.Allocator` (`src/Modules/Allocator`): bağımlılıkları **yalnızca Kernel ve Ai**. Kaynaklarında `Npgsql`, `System.Net.Http`, `System.Diagnostics.Process` ve HoldoutEvaluator kullanılamaz (Researcher'daki kaynak taraması deseni).
- Execution'ın bağımlılıkları **{Kernel, MarketData}** olarak kalır. Execution yalnızca `IAllocationAdvisor` portunu ve DTO'larını tanımlar.
- Trading.Host; Allocator, Ai ve Ai.Gemini'yi bağlar. Research, Researcher, SourceGateway ve HoldoutEvaluator'a ulaşamaz (mimari test).
- Gemini runtime anahtarı yalnızca Trading.Host'un Docker secret dosyasındadır (`gemini_runtime_api_key`); kullanıcı `m8-update.ps1` ile yerleştirir. Anahtar yoksa advisor her zaman T11'e düşer.
- HTTP: yalnızca `GeminiProvider`'ın guard'ı (`generativelanguage.googleapis.com`, HTTPS, redirect reddi). Testnet guard'ı değişmez.

## 3. Motor değişiklikleri (Kernel)
### 3.1 İki fazlı adım
- `BeginStep(t, bars, reference, context) → EntryCandidate[]`: gap, açılış, mum içi stop, çıkış sinyalleri, trailing stop ve giriş adaylarına kadar her şeyi yapar. Aday listesi (sleeve, sembol, T11 ağırlığı, σ) döner.
- `CompleteStep(AllocationOverrides?)`: giriş yolunun geri kalanını (ağırlık → tavanlar → `PendingEntries`) tamamlar.
- `Step(...)` = `BeginStep` + `CompleteStep(context.Allocation)`. Yalnızca yeniden düzenlemedir: override null iken çıktı ve hash'ler birebir aynıdır.

### 3.2 Override'ın uygulanması
- `StepContext.Allocation`: `(sleeve, symbol) → { Multiplier }` ya da `{ Veto }`. JSON'da null ise yazılmaz; mevcut adım JSON'u ve `StepInputs` hash'leri değişmez.
- Uygulama yeri: T11 ağırlıkları hesaplandıktan sonra, hedef nominaller hesaplanmadan önce; **yalnızca o adımın giriş adaylarına**.
  1. `m` [0,5; 1,5] aralığına clamp edilir (Kernel de bandı uygular; savunma derinliği);
  2. `w' = min(m × w_T11, MaxAssetWeight)`;
  3. ardından mevcut total-exposure ölçeklemesi, asset, deneysel (%10) ve küme (D29) tavanları **değişmeden** uygulanır.
- Veto: aday `Rejected "allocator-veto"` olur.
- Aday olmayan (açık pozisyon, bekleyen çıkış, sinyalsiz) sembol için gelen override **yok sayılır** ve `allocator-ignored` olarak kaydedilir.
- `UnitWeight` olayı (L1) T11 ağırlığını taşımaya devam eder. Ayarlama yeni **L2** olayıyla kaydedilir: `AllocatorAdjusted` (Before = T11 ağırlığı, After = uygulanan ağırlık).

### 3.3 Değişmezler (testle)
| # | Değişmez |
|---|---|
| I-1 | Override null iken tüm hash'ler (36 engine, M2'nin dördü, parity L1/L2, paper adım JSON'u) birebir aynı |
| I-2 | Override'ın uygulandığı adımda çıkış, stop, `StopRaised`, `ExitSignal` ve `ExitFill` olayları T11 koşusuyla birebir |
| I-3 | Hiçbir override bir girişi asset, total, deneysel ya da küme tavanının üzerine çıkaramaz |
| I-4 | Uygulanan çarpan her zaman [0,5; 1,5] içinde ya da veto |
| I-5 | Testnet'te çıkış emirleri ve `SyncStopsAsync` override'dan etkilenmez |

## 4. Modlar ve durum makinesi
### 4.1 Modlar
| Mod | LLM çağrısı | Gerçek emirlere etkisi |
|---|---|---|
| `Disabled` (varsayılan) | Yok | Yok (T11) |
| `Shadow` | Yeterlilik modunda (router `ExecuteForQualificationAsync`, `JobKind.Qualification`), kayıtlı protokolle | **Yok.** Öneri yalnızca gölge motora uygulanır |
| `Active` | Production modunda (`qualifiedModels`, `JobKind.Allocation`) | Yalnızca `Validated` stratejilerin girişlerine; diğerleri T11 |

### 4.2 Geçişler (`trading.allocator_mode_events`, append-only, BEFORE INSERT kural trigger'ı)
| Geçiş | Koşul | Yetki |
|---|---|---|
| `Disabled → Shadow` | `confirm: true`, gerekçe, kayıtlı allocator protokolünün hash'i | Kullanıcı (`ops_api`) |
| `Shadow → Shadow` | Protokol değişimi (pilot → yeterlilik); yeni kayıtlı protokol hash'i | Kullanıcı |
| `Shadow → Active` | `confirm: true`; `Passed` bir AllocationProposal `TaskQualification`'ı; yeterlilik penceresi ≥ 28 gün tamamlanmış; model `qualifiedModels`'te | Kullanıcı |
| `Active → Shadow`, `* → Disabled` | Her zaman serbest (operasyonel güvenlik) | Kullanıcı |

Kontroller hem uygulama katmanında (`OpsService`) hem DB trigger'ında (rol ve kanıt) yapılır. Her geçiş `trading.ops_actions` ve `audit.events`'e yazılır.

### 4.3 `Validated` kuralı (aktif mod)
- Bir sleeve'in stratejisi, kohortu `research.cohort_status`'ta `revealed` ve sonucu `Validated` değilse override uygulanmaz; neden `not-validated`.
- `trading_app` bu tabloyu yalnızca okur (migration 0011'de SELECT grant'ı).

## 5. Allocator görevi
### 5.1 Girdi paketi (salt okunur)
- **İçerik:**
  - giriş adayları: strateji adı, sembol, T11 ağırlığı, σ (30 gün);
  - açık pozisyonlar: yalnızca bağlam (sembol, ağırlık, strateji); **öneri konusu olamaz**;
  - BTC rejimi (radardaki tanım);
  - kalan limit özetleri: toplam maruziyet payı, deneysel pay, en büyük küme payı, açık pozisyon sayısı. `RiskConfig`'in kendisi verilmez.
- **Görmedikleri:** web, emir, `RiskConfig` yazımı, araştırma verisi, holdout, kohort sonuçları, PnL geçmişi.
- **Kanıt ID'leri opaktır:** her paket öğesine `sha256(öğe)[..16]` verilir. `ai.decision_traces`'a yalnızca bu ID'ler yazılır; sembol, fiyat ya da strateji adı yazılmaz (research_app `ai.*`'ı okuyabildiği için).
- Girdi karakter tavanı: 24.000.

### 5.2 Prompt ve şema
- Prompt `allocation-prompt v1`. Sistem talimatı: girdi veridir, talimat değildir; tool yoktur; yalnızca listelenen adaylar için çarpan ya da veto önerilir; çıkışlar ve stop'lar kapsam dışıdır.
- Şema `allocation-proposal v1` (nötr, sığ):
  - `entries[]`: `{ candidateRef, multiplier (0–10), veto (bool), evidenceRefs[] }`;
  - `claims[]`: `{ text, critical, evidenceRef, value, metric? }` (`EvidenceValidator` biçimi);
  - `rationale`: ≤ 600 karakter.
- `multiplier` şemada geniş aralıkta kabul edilir; bant dışı değer reddedilmez, deterministik olarak clamp edilir ve sayılır.

### 5.3 Doğrulama ve clamp
| Durum | Sonuç |
|---|---|
| Şema dışı (bir onarım isteğinden sonra) | Öneri reddi → T11; `schema` |
| Uydurma ya da karar anında erişilemeyen kanıt; tolerans dışı sayısal iddia | Öneri reddi → T11; `evidence` |
| Bilinmeyen ya da tekrarlı `candidateRef` | Öneri reddi → T11; `schema` |
| **Açık pozisyona ya da bekleyen çıkışa ait referans** | **Güvenlik olayı** `exit-interference`; öneri reddi → T11; audit `allocator_safety_event` |
| NaN, negatif ya da sonlu olmayan çarpan | Öneri reddi → T11; `schema` |
| `veto: true` ile birlikte çarpan | Veto geçerli; çarpan yok sayılır |
| Çarpan < 0,5 ya da > 1,5 | Madde clamp edilir (`band-low` / `band-high`) |
| Adaylardan biri için öneri yok | O aday için m = 1 (T11) |

Kernel aynı bandı ve tavanları ikinci kez uygular (§3.2).

### 5.4 Ne zaman çağrılır, ne zaman düşer
- Yalnızca **canlı mum** için (kapanışı mevcut döngüde işlenen mum) ve en az bir giriş adayı varsa. Catch-up mumlarında T11 kullanılır (`catch-up`).
- Timeout 30 sn (A-6). Çıkışlar ve stop'lar LLM çağrısından **önce** işlenir; gölgede çağrı gerçek adım commit edildikten sonra yapılır.
- Fallback nedenleri: `ai-kill-switch`, `no-qualified-model`, `no-key`, `timeout`, `provider-error`, `schema`, `evidence`, `safety`, `budget`, `catch-up`, `not-validated`, `disabled`.
- Her öneri ve fallback `trading.allocator_proposals`'a yazılır.

## 6. Yeterlilik (testing-strategy §6.2; `QualificationDesign.NonInferiority`)
### 6.1 Pilot (A-7)
- 14 gün gölge mod; ayrı bir pilot protokolü ve iş (`alloc-pilot-<hash12>`), bütçe 1,00 USD.
- Pilot yalnızca §6.3'teki σ̂_d'yi ve operasyonel oranları ölçer; **nitelik vermez**. Pilot ve yeterlilik pencereleri zamanca ayrıktır.

### 6.2 Karar kuralı (pilottan önce sabit)
- **Birim:** paper run başına UTC günü. `d_t` = o gün gölge motorun log getirisi − gerçek (T11) run'ın log getirisi; Kohort 0 ve Kohort 1 paper run'larının günlük ortalaması. Testnet ayna run'ları hariç.
- **İstatistik:** `d̄`; Kernel'deki `StationaryBootstrap` (ortalama blok uzunluğu `⌈n^(1/3)⌉`, 10.000 tekrar, seed protokolde) ile tek yönlü (1 − α) alt sınır `L`; α = 0,05.
- **Karar sırası:**
  1. herhangi bir güvenlik olayı (`exit-interference`, şema dışı çıktının kabul edilmesi, uydurma kanıtın geçmesi, izin dışı tool) → `Failed`;
  2. pencere eksik (bütçe, kill switch, kesinti nedeniyle gün sayısı `N`'nin altında) ya da geçerli öneri < 10 (A-5) ya da süre kuralı `N > 56` verdi → `Inconclusive`;
  3. geçerli öneri oranı < %80 (A-3) → `Failed`;
  4. `L > −m` (A-1) **ve** her run'da `MDD_gölge − MDD_T11 ≤ 0` (A-2) → `Passed`;
  5. aksi halde `Failed`.
- **Kayıt:** sonuç ne olursa olsun `TaskQualification`'a yazılır. AllocationProposal satırında yalnızca karar, gün ve öneri sayıları, güvenlik olayları ve maliyet bulunur; `PValue` null'dır. `L`, `d̄` ve drawdown değerleri yalnızca `trading.*` tarafında ve kullanıcıya verilen rapordadır.
- **`Passed`'in anlamı:** yalnızca "T11'den marjdan fazla kötü değil" demektir; değer katkısı iddiası değildir. `qualifiedModels` girişi ve `Shadow → Active` ayrı kullanıcı kararlarıdır.

### 6.3 Süre kuralı (A-4)
- `N = max(28, ⌈(2,487 · σ̂_d / m)²⌉)` gün; `2,487 = z₀,₉₅ + z₀,₈₀`, `m = 0,03 / 365`, σ̂_d pilotun günlük fark standart sapması.
- σ̂_d = 0 ise (pilotta allocator hiçbir girişi değiştirmediyse) `N = 28`.
- `N > 56` ise pencere 56 gündür ve sonuç en iyi ihtimalle `Inconclusive` olur.
- `N`, `alloc-report` tarafından hesaplanır ve yeterlilik protokolüne yazılır; σ̂_d raporun yalnızca kullanıcıya açık bölümündedir.

### 6.4 Bütçe
- Tahmin: çağrı başına ≤ 0,006 USD üst sınır; günde en fazla 6 canlı 4h mum × 2 run = 12 çağrı.
- İş bütçesi her çağrıdan önce ledger'daki iş toplamıyla kontrol edilir; aşılacaksa çağrı yapılmaz (`budget`), gölge T11 ile devam eder.

## 7. Görünürlük ve sızıntı kuralları
- **Claude Code** gölge ve yeterlilik raporlarından yalnızca operasyonel alanları görür: karar, gün sayısı, çağrı, geçerli öneri, clamp, veto, fallback ve güvenlik olayı sayıları; şema ve kanıt geçme oranları; maliyet; süre `N`.
- Getiri farkı, `L`, σ̂_d ve drawdown değerleri yalnızca kullanıcı içindir; repoya yazılmaz, araştırma ya da hipotez girdisi olamaz.
- `research_app`, allocator tablolarını okuyamaz (test). Allocator izleri `ai.*`'ta yalnızca opak kanıt ID'leri taşır (test).
- Dashboard `/allocator` uç noktası gerçek ortamda Claude Code tarafından açılmaz (ADR-0020 §6 ile aynı).
- `scripts/m8-*.ps1` ve `--allow-network` komutları yalnızca kullanıcı tarafından çalıştırılır.

## 8. Ön kayıt blokları (v1.1: hash'lerle tamamlandı)
```json allocator-pilot
{
  "task": "AllocationProposal",
  "provider": "gemini",
  "model": "gemini-3.5-flash-lite",
  "promptVersion": "allocation-prompt v1",
  "promptHash": "a35e1db64bc413ba163770b396a54fd39b1fea66b4bdb169b12e2163f8d1614f",
  "schemaHash": "3dba851b0cfb0f4f1a18f5ad7d8a99fb7e4e11c8ee8322922660e2aa44856280",
  "band": "allocator-band v1",
  "days": 14,
  "budgetUsd": 1.00,
  "timeoutSeconds": 30
}
```

- Prompt hash'i (`allocation-prompt v1`, sistem talimatının SHA-256'sı): `a35e1db64bc413ba163770b396a54fd39b1fea66b4bdb169b12e2163f8d1614f`.
- Şema hash'i (`allocation-proposal v1`): `3dba851b0cfb0f4f1a18f5ad7d8a99fb7e4e11c8ee8322922660e2aa44856280`.
- Pilotun kaydedilecek belgesi [`m8-alloc-pilot-protocol.md`](m8-alloc-pilot-protocol.md)'dir (`json allocation-protocol` bloğu; rol `pilot`, 14 gün, 1,00 USD, seed 83000). Bir test, bu bloğun ve yukarıdaki hash'lerin koddaki prompt, şema ve bantla aynı olduğunu doğrular (`AllocatorProtocolTests`).

Yeterlilik protokolü (`docs/m8-alloc-qualification-protocol.md`) C3'te, §6.2–§6.3'teki kural ve pilottan hesaplanan `N` ile yazılır; kural değiştirilemez. Aynı `json allocation-protocol` biçimindedir (rol `qualification`, `days` = `N`, `N > 56` ise `days` 56 ve `"underpowered": true`).

## 9. Kapı C (kullanıcının koşuları)
| # | Adım | Komut | Tahmini maliyet |
|---|---|---|---|
| C0 | Migration 0011, Gemini runtime anahtarı secret'ı, Trading.Host yeniden derleme. Kontrol: mod `Disabled`, döngüler `ok`, bellek < 384 MiB | `m8-update.ps1` | 0 |
| C1 | Sentetik spike: şema Gemini'ye sığıyor mu, `modelVersion` eşleşmesi | `m4-ai.ps1 alloc-spike --allow-network` | < 0,05 USD |
| C2 | Pilot protokolünün kaydı; `Disabled → Shadow`; 14 gün | `m4-ai.ps1 alloc-register`, `m8-allocator-mode.ps1` | ≤ 1,00 USD |
| C3 | Pilot raporu (operasyonel alanlar); `N`; yeterlilik protokolünün commit'i ve kaydı; `Shadow → Shadow` | `m8-shadow-report.ps1`, `alloc-register`, `m8-allocator-mode.ps1` | — |
| C4 | M8 kabul raporu ve kapanış (U-M8-4) | — | — |
| sonra | Pencere bitince `alloc-qualify` → `TaskQualification`; `Passed` ise `qualifiedModels` ve `Shadow → Active` ayrı kullanıcı kararları | `m4-ai.ps1 alloc-qualify` | toplam ≤ 3,00 USD |

## 10. Kabul kriterleri (roadmap M8)
| Kriter | Kanıt |
|---|---|
| Limit dışı öneriler clamp ediliyor ya da reddediliyor | §5.3 tablosu testleri; I-3, I-4 property testleri |
| Çıkışları engelleme girişimi etkisiz | I-2, I-5; `exit-interference` testleri; kalibrasyon kolu "Attacker" |
| LLM durdurulduğunda ya da hata verdiğinde varsayılan politika devreye giriyor | Fault injection: kill switch, timeout, 5xx, refusal, şema, kanıt, bütçe, anahtar yok |
| Gölge mod ≥ 4 hafta tamamlandı | C2–C3 ve yeterlilik penceresi (U-M8-4: kabul edilen sapma) |
| AllocationProposal protokolü ve sonucu kaydedildi | Pilot ve yeterlilik protokolleri; `TaskQualification` (pencere sonunda) |
| Aktif moda geçiş yalnızca nitelik varsa ve kullanıcı onayıyla | Mod geçiş testleri (uygulama ve DB trigger'ı) |
| Allocator ağırlığı yalnızca `Validated` stratejilere | `not-validated` testleri; aktif replay ve `ParityReplay` birebir |

## 11. Kapı B uygulama notları (v1.1)
- **Kod yerleşimi (ADR-0023):**
  - Kernel: `Backtest/Allocation.cs` (`AllocationOverride`, `EntryCandidate`, `AllocatorBand`, `AllocatorMode`, `AllocationRequest`, `AllocationDecision`, `AllocationOutcome`, port `IAllocationAdvisor` ve `DeterministicAllocationAdvisor`); `PortfolioEngine.BeginStep`/`CompleteStep`; L2 olayları `AllocatorAdjusted`, `AllocatorIgnored`.
  - `src/Modules/Allocator`: `AllocationPrompt`, `AllocationPacket`, `AllocationOutputValidator`, `AllocationProposalReader`, `AllocatorGate`, `AllocationAdvisor`, `NonInferiority`, `AllocatorProtocol`, `ShadowEvaluation`.
  - Execution: `AllocatorStore` (Npgsql ve bellek içi), `PaperTrader` gölge motoru ve aktif mod, `StepInputs.Allocation`, `OpsService.SetAllocatorModeAsync`, `DashboardQueries.AllocatorAsync`.
  - Trading.Host: `TradingHost.CreateAllocator`, `GET /allocator`, `POST /allocator/mode`, "Allocator" sekmesi.
  - Ai: `JobKind.Allocation` (iş payı 0,10), `QualificationDesign.NonInferiority` (harness bu tasarımı çalıştırmaz).
  - AiCli: `alloc-register`, `alloc-spike`, `alloc-report`, `alloc-qualify`; script'ler `m8-update.ps1`, `m8-allocator-mode.ps1`, `m8-allocator.ps1` (yalnızca kullanıcı).
  - Migration 0011.
- **Uygulama ayrıntıları ve sapmalar (T):**
  - **Sınıflandırma sırası:** bir öneri birden çok kurala takılırsa sonuç sırasıyla güvenlik, şema, kanıttır (§5.3 tablosundaki kategoriler değişmedi).
  - **Mod kuralları ve `ops_api`:** trigger ve `OpsService`, yeterlilik kayıtlarını iki `SECURITY DEFINER` fonksiyonla (`trading.allocator_protocol_registered`, `trading.allocator_qualification_passed`) evet/hayır olarak okur. `ops_api`'nin `ai.qualification_protocols` ve `ai.task_qualifications` üzerinde tablo yetkisi yoktur (M5b'deki dashboard kuralı korunur).
  - **`Active → Shadow`:** gölge çalışmak için de kayıtlı bir protokol gerekir; `* → Disabled` her zaman serbesttir.
  - **Rapor ve yeterlilik kimliği:** `trading_app` ve `ops_api` yalnızca Trading.Host container'ından bağlanabildiği için (`pg_hba`), `alloc-report` ve `alloc-qualify` `m5-migrate.ps1` gibi `ops_owner` ile loopback'ten çalışır. Yalnızca `trading.allocator_*` ve ledger'ı okur, `ai.task_qualifications`'a tek satır ekler.
  - **`TaskQualification`:** AllocationProposal satırında `p_value` `NaN`'dir; `trials_llm` çağrı, `successes_llm` geçerli öneri, `trials_baseline` gün sayısıdır; `notes` yalnızca karar nedenidir.
  - **Zaman aşımı:** A-6 (30 sn), `ai.v1.json`'daki AllocationProposal politikasına yazıldı (yalnızca bu görev).
  - **Spike (C1):** üretim yolunun aynısıyla (paket, router yeterlilik modu, doğrulayıcı, clamp) üç sentetik karar; bellek içi spike protokolü, iş bütçesi 0,05 USD; yalnızca sentetik semboller.
  - **Güvenlik olayı kaydı:** `exit-interference`, `trading.allocator_proposals.safety` alanına ve `audit.events`'e (`allocator_safety_event`) yazılır. `ai.decision_traces`'te yalnızca indekslerle görünür (`exit-interference: entry i`); referans ya da sembol yazılmaz.
  - **Pencere:** gölge adımları ve kararlar protokol hash'iyle etiketlenir; pencere, o hash'i taşıyan satırlardır. Günlük işaret, 20:00 açılışlı mumdan sonraki değerdir (UTC gün sonu).
- **Regresyon kilitleri (Claude Code, `R`, 2026-10-29):**
  - M2'nin dört sonuç hash'i (`m5-research.ps1 parity-m3`): kodlanmış ve DSL baseline'lar birebir;
  - 36 engine regresyon hash'i (`EngineRegressionTests`) değişmedi;
  - kuru `parity-m5`: Kohort 0 L1 `f870c0a658b1`, L2 `b078ebaa6fc4`; Kohort 1 L1 `8dccb8f86868`, L2 `3bcc41534577`; rapor özeti M5b'dekiyle aynı (`2f234be0…`).
