# ADR-0023: Portfolio Allocator'ın yerleşimi, iki fazlı adım ve gölge mod

- Durum: **Kabul edildi (Kapı A, kullanıcı onayı 2026-10-29)** · Tarih: 2026-10-29
- Karar türü:
  - **U:** `m8-protocol.md` U-M8-1…U-M8-5 (D34: gölge kapsam, bant, kapanış, model ve bütçe) ve A-1…A-7 (2026-10-29).
  - **T:** modül yapısı, bağımlılık kuralının değişmesi, iki fazlı adım, gölge motor, mod durum makinesi, sızıntı korumaları.
- Uygulama durumu: **Kapı B'de çevrimdışı uygulandı (2026-10-29; `verify.ps1` yeşil, 770 test; API maliyeti 0).** Gerçek gölge mod Kapı C'de.
- Uygulama notları (Kapı B; ayrıntı `m8-protocol.md` §11):
  - Port (`IAllocationAdvisor`) ve DTO'lar Kernel'dedir: Execution ve Allocator yalnızca Kernel üzerinden buluşur; Execution'ın bağımlılıkları {Kernel, MarketData} kalır.
  - Mod kuralları yeterlilik kayıtlarını iki `SECURITY DEFINER` fonksiyonla okur; `ops_api`'ye `ai` tablolarında yeni yetki verilmedi.
  - Rapor ve yeterlilik komutları `ops_owner` ile çalışır (`pg_hba`: `trading_app` yalnızca Trading.Host container'ından).
  - Gemini runtime anahtarı Compose secret'ıdır (`gemini_runtime_api_key`, boş yer tutucu: her karar T11).
- İlgili: ADR-0005, ADR-0010, ADR-0011, ADR-0015, ADR-0017, ADR-0018, ADR-0020; `m8-protocol.md`; `architecture.md` §2–§5

## Bağlam
- `architecture.md` §4, Allocator'ı Trading'e yerleştirir. Ama ADR-0018 §4 ve `DependencyRuleTests` (`Trading_reaches_only_the_kernel_market_data_and_infrastructure`), Execution ve Trading.Host'un Ai modülüne ulaşmasını yasaklar.
- T11 ağırlıkları `PortfolioEngine`'in tek adımı içinde, yalnızca giriş adayları için hesaplanır. Aktif modda LLM önerisi bu hesaptan sonra, girişler tamamlanmadan önce gerekir.
- `UnitWeight` olayı L1 parity hash'ine girer. LLM ağırlığı bu olaya yazılırsa parity bozulur.
- Router'ın gölge modu yoktur. Nitelikli olmayan bir modele tek yol yeterlilik modudur (ADR-0017 §3). testing-strategy §6.2'ye göre AllocationProposal yeterliliği zaten ileriye dönük gölge moddur.
- `research_app`, `ai.*` tablolarını okuyabilir (migration 0006). Canlı veriden türeyen bilgi bu tablolara yazılırsa araştırmaya sızar.

## Karar

### 1. Modül ve bağımlılıklar
| Proje | Bağımlılıklar | İçerik |
|---|---|---|
| `PortfolioIntelligence.Allocator` (`src/Modules/Allocator`, yeni) | Kernel, Ai | Girdi paketi, prompt, şema, çıktı doğrulayıcı, clamp, `AllocatorGate`, `AllocationAdvisor`, `NonInferiorityEvaluator` |
| `Execution` | Kernel, MarketData (değişmez) | `IAllocationAdvisor` portu ve DTO'ları, gölge motor, mod okuma, kalıcılık |
| `Trading.Host` | + Allocator, Ai, Ai.Gemini | Bağlama (ledger → kill switch → GeminiProvider → guard → router → advisor) |

**Mimari testlerle korunan kurallar:**
- Execution'ın geçişli bağımlılıkları tam olarak {Kernel, MarketData}.
- Trading.Host; Research, Research.Host, Researcher, SourceGateway ve HoldoutEvaluator'a ulaşamaz. Ai ve Ai.Gemini'ye yalnızca Trading.Host ulaşabilir (Execution ulaşamaz).
- Allocator yalnızca Kernel ve Ai'a bağlıdır; kaynaklarında `Npgsql`, `System.Net.Http`, `System.Diagnostics.Process` ve HoldoutEvaluator yoktur (yerleştirilmiş ihlalle öz-test edilen kaynak taraması).
- Ai, Execution'a ve Trading.Host'a ulaşamaz (değişmez).
- `Google.GenAI` yalnızca Ai.Gemini'dedir (değişmez).

### 2. İki fazlı adım
`PortfolioEngine.Step` = `BeginStep` + `CompleteStep(overrides)`. `BeginStep` çıkışları, stop'ları ve giriş adaylarını hesaplar; `CompleteStep` yalnızca giriş yolunu tamamlar. Override null iken davranış birebir aynıdır; 36 engine hash'i ve M2'nin dört hash'i bunun kilididir. Override yalnızca giriş adaylarına uygulanır, Kernel bandı ve tüm tavanları kendisi yeniden uygular. `UnitWeight` (L1) T11 ağırlığını taşır; ayarlama yeni L2 olayı `AllocatorAdjusted` ile, veto `Rejected "allocator-veto"` ile kaydedilir.

### 3. Gölge motor
Her paper run için bellek içinde ikinci bir `PortfolioEngine` tutulur. Gerçek adım commit edildikten **sonra** aynı mumla ilerletilir: kendi adayları → advisor → `CompleteStep(override)`. Gerçek emirler, ledger ve hash'ler etkilenmez. Override'lar `trading.allocator_shadow_steps`'e yazılır; restart'ta gölge motor adım günlüğü ve bu override'larla, LLM çağrılmadan yeniden üretilir ve durum hash'i doğrulanır. Gölge çağrıları router'ın yeterlilik modundan, kayıtlı protokol ve `JobKind.Qualification` ile yapılır; bu modda `MayAffectDecisions` her zaman false'tur.

### 4. Aktif mod
`BeginStep` → advisor (yalnızca `Validated` sleeve'ler, production modu, `JobKind.Allocation`) → `CompleteStep`. Uygulanan override `StepInputs`'a yazılır; restart replay'i ve `ParityReplay` aynı girdiyle LLM'siz çalışır. Testnet run'ında aynı (strateji, sembol) çarpanı kullanılır.

### 5. Mod durum makinesi
`trading.allocator_mode_events` (append-only, BEFORE INSERT kural trigger'ı; `ops_api` rolü ve kanıt). Geçişler `m8-protocol.md` §4.2'de. `* → Disabled` her zaman serbesttir.

### 6. Sızıntı korumaları
- Allocator izleri `ai.decision_traces`'a yalnızca opak kanıt ID'leriyle yazılır.
- Allocator tabloları `trading` şemasındadır; `research_app`'in erişimi yoktur.
- AllocationProposal `TaskQualification` satırı performans değeri taşımaz (`PValue` null).

## Gerekçe
- **Trading.Host içinde, ayrı process değil:** aktif modda öneri, aynı adımın iki fazı arasında gerekir. Ayrı bir process DB üzerinden asenkron çalışır; zamanlama, bellek (16 GB, ADR-0013) ve yeniden üretilebilirlik açısından daha zayıftır. Execution'ı AI'dan arındırılmış tutmak ve AI'ı yalnızca host bağlamasında görmek, kuralın amacını (emir yolunun AI'a bağımlı olmaması) korur.
- **İki fazlı adım:** tek motor ve tek kod yolu korunur; parity, aynı kodun aynı girdilerle çağrılmasına dayanmaya devam eder (ADR-0018).
- **Gölge motor:** karşı-olgusal performans ancak aynı mumlarla, aynı motorla ve override'la ilerleyen ayrı bir durumdan ölçülebilir. Gerçek motora dokunmadığı için gölge modun güvenlik maliyeti sıfırdır.
- **Yeterlilik modu:** gölge mod tanım gereği yeterlilik koşusudur; router'a yeni bir mod eklemek yerine mevcut, test edilmiş yol kullanılır.

## Riskler
- **Düşük işlem sıklığı:** Kohort 0'ın giriş sayısı azdır; yeterlilik sonucunun `Inconclusive` çıkması olasıdır. Bu durumda T11 kalıcı olur (D20).
- **Non-inferiority'nin sınırı:** bant dar olduğu ve allocator az değişiklik yaptığı sürece fark küçük kalır; `Passed` değer katkısını göstermez. Geçerli öneri oranı şartı, sürekli fallback'e düşen bir modelin T11'le eşleşerek geçmesini önler.
- **Trading.Host'un bağımlılık yüzeyi büyür:** Gemini SDK'sı ve anahtarı Trading.Host'a girer. Anahtar yalnızca Docker secret'ındadır; HTTP yalnızca Gemini guard'ından çıkar; bellek C0'da ölçülür.
- **Canlı verinin LLM'e gitmesi:** architecture §4'ün izin verdiği salt-okunur özetlerle sınırlıdır; ücretli katmanda içerik ürün geliştirmede kullanılmaz (S-G5).

## Alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| Ayrı bir Allocator process'i (DB üzerinden öneri) | Aktif modda aynı adımın içinde zamanlama; ek container belleği; asenkron yeniden üretim |
| Execution'a Ai referansı | Emir yolu AI modülüne bağımlı olur; daha geniş bir kural gevşemesi |
| LLM ağırlığını `UnitWeight`'e yazmak | L1 parity hash'i bozulur |
| Router'a yeni `Shadow` modu | Gölge mod zaten yeterliliktir; mevcut yeterlilik yolu test edilmiş |
| Gölge modda yalnızca `Validated` stratejiler | Bugün `Validated` strateji yok; yeterlilik verisi 2027'ye kadar oluşmazdı (U-M8-2) |
