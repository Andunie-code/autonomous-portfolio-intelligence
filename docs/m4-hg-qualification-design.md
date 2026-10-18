# HypothesisGeneration yeterliliği — tasarım ve pilot ön kaydı (v1)

- **Durum:** Ön kayıt — kullanıcı tarafından onaylandı (2026-10-19, U), asgari etki δ = 0,15 dahil. Pilot ön kaydının kanıtı, bu belgenin eklendiği git commit'idir; pilot o commit'ten sonra koşulur. Bu belge **yeterlilik protokolü değildir.** Örneklem büyüklüğü, yeterlilik seed'leri ve bütçe pilottan sonra `m4-hg-qualification-protocol-v1.md`'de, bu belgedeki kurallarla sabitlenir (ADR-0010, testing §6.1).
- İlgili: ADR-0010, ADR-0017, `m4-protocol.md` §8, `testing-strategy.md` §6, `m3-protocol-v2.md`, `m3-calibration-results-v2.md`, `m4-spike-results.md`.
- Karar türleri: **U** = kullanıcı, **T** = teknik lider.

## 1. Amaç ve kullanıcı kararı
- **Soru:** HypothesisGeneration görevinde `gemini-3.5-flash-lite`, aynı bilgiyle çalışan deterministik bir grid baseline'ından **anlamlı ölçüde daha sık** araştırma kapılarını geçen hipotez üretiyor mu?
- **Karar (U, 2026-10-19):** pilot ve yeterlilik koşuları **seed'li sentetik evrenlerde** yapılır.
  - Hiçbir deneme `research.trials`'a yazılmaz. Global sayaç 98'de kalır ve gerçek adayların G6 eşiği etkilenmez.
  - Bu, kalibrasyondaki kuralla aynıdır (m3 §7: sentetik koşular deneme sayacına girmez).
  - Ek fayda: sentetik veride modelin geçmiş piyasa bilgisi işe yaramaz, bilgi kontaminasyonu yoktur (`m4-protocol.md` §8).
- **Neden "keşif ve değerlendirme ayrı" (T):** model, bir evrenin deney sonuçlarını görür; önerisi aynı yapıdaki başka bir seed'li evrende değerlendirilir. Böylece keşif verisine aşırı uyum ödüllendirilmez; ölçülen şey, yapıyı yakalayan bir hipotez üretme yeteneğidir.

## 2. Öğe (item) tanımı: `hg-synthetic v1` (T)
- **Öğe:** bir seed `s`.
  - Keşif evreni: `SyntheticMarkets.Trending(s, κ = 0,056, ortalama rejim 300 mum, yukarı payı 0,5)`. Bu, kalibrasyonun doğru pozitif yapısıdır (v2).
  - Değerlendirme evreni: aynı parametrelerle `Trending(s + 500.000, …)`.
  - Her ikisi de 20 sentetik parite (biri `BTCUSDT` adlı referans seri), 4h, 4 yıllık araştırma penceresi ve ısınmadan oluşur.
- **Keşif deneyleri** (modelin gördüğü kanıt), keşif evreninde `no-halt` ve maliyet modeli v1 ile:
  - `exp:1` `ema-trend` ve `exp:2` `supertrend` (M2 baseline'ları);
  - `exp:3`–`exp:6`: `RandomStrategies(s)` ile çekilen 4 rastgele DSL stratejisi.
- **Gösterilen metrikler:** `M` (6 ondalık), işlem sayısı, pozitif yıl sayısı, kârlı parite sayısı, maksimum drawdown ve tanım. Ham veri gösterilmez.
- Öğe içeriği kendini tanımlar (`hg-synthetic v1; seed=…; kappa=0.056; meanBars=300; upShare=0.5; evalSeedOffset=500000`). Set hash'i bu içeriklerden hesaplanır (`QualificationItem.SetHash`).

## 3. Kollar (T)
- **LLM kolu:**
  - Model `gemini-3.5-flash-lite` (D12; spike'ta doğrulandı); prompt sürümü `hg-synthetic-prompt v1`.
  - Çıktı şeması: `title`, `rationale`, `definition` (DSL JSON metni), `claims`. Şema hash'i `770469e889e9bea7ab11cc26e8d02621cfe1800f96afed2fe826c27b0d6ad1fa`.
  - **Tool yok**, yalnızca structured output. Router qualification modunda çalışır; `maxOutputTokens` 8192, timeout 120 sn, tek şema onarım isteği.
  - `EvidenceValidator`: `evidenceRef` bir keşif kimliği olmalı; atıf yapılan sayı, gösterilen `M` değerine göreli %0,5 içinde olmalı; referanssız kritik iddia reddedilir.
  - Tamamlanmamış, degraded, reddedilmiş ya da geçersiz DSL içeren yanıt **başarısızlık** sayılır.
- **Baseline kolu (deterministik grid):** keşifte `M`'si en yüksek tanım (eşitlikte küçük kimlik), ilk parametre komşusuyla (G4'teki ±%10 sırası) bir adım kaydırılır. Ücretsizdir ve aynı bilgiyi kullanır. LLM ancak bu alternatifi geçerse değer katar.
- **Başarı (her iki kol için aynı):** öneri, değerlendirme evreninde, `no-halt` koşusunda, v2 eşikleriyle (`N_min = 100`, `τ_wf = 0,75`, `τ_nb = 0,80`, `τ_dsr = 0,90`; DSR'de `N = 100`, `V = 1/(T − 1)`) **G1–G6'nın tamamını** geçer. Bootstrap B = 10.000.

## 4. Birincil metrik ve karar kuralı (T)
- **Birincil metrik:** öğe başına başarı oranı. Test, tek yönlü Fisher exact (H1: LLM oranı > baseline oranı).
- **α = 0,05**, **hedef güç 0,80.**
- **Asgari anlamlı etki δ = 0,15** (oranlar arası mutlak fark). Pilottan önce sabitlenir.
  - Gerekçe: LLM kolu maliyet, nondeterminizm ve bir injection yüzeyi getirir.
  - Deneme başına 15 puandan küçük bir artış, deterministik grid yerine LLM'i karar hattına sokmayı haklı çıkarmaz.
  - Bu bir mühendislik değer yargısıdır, istatistiksel değildir.
- **Karar:**
  - Herhangi bir güvenlik ihlali → `Failed`.
  - Ön kayıtlı örneklem tamamlanmadıysa (bütçe ya da kill switch) → `Inconclusive`.
  - Aksi halde `p < α` **ve** oran farkı ≥ δ → `Passed`; değilse `Failed`.
  - Sonuç ne olursa olsun `TaskQualification`'a yazılır.
- **`Passed`'in anlamı:** karar etkileyen görev politikasına `qualifiedModels` girişi eklemeyi mümkün kılar. Bu ekleme ayrı bir kullanıcı onayıyla yapılır; ayrıca yalnızca bu görev tanımı (sentetik öğe, bu prompt, bu şema) için geçerlidir. Model, prompt, şema ya da set değişirse nitelik düşer.

## 5. Pilot (ön kayıt; nitelik vermez) (T)
- **Set:** seed 30000–30019, 20 öğe. Set hash'i `e72eeccc71694dfc66cf75ff90fb688c931284ac5f3fb6933e4f337d74cc7efd`.
- **Bütçe:** 0,50 USD (pilot işi). Kill switch ve tüm tavanlar aynen geçerlidir.
- **Ölçülenler:** iki kolun başarı oranı (p̂_LLM, p̂_base), geçersiz DSL oranı, tanımsız yanıt sayısı, keşif kopyası oranı, öğe başına maliyet, güvenlik ihlali sayısı.
- Pilot, `TaskQualification` kaydı **üretmez** ve hiçbir nitelik vermez. Çağrılar `DecisionTrace`'e (mod `Qualification`) ve ledger'a yazılır.
- Pilot bir kez koşulur. Sonuçlar `docs/m4-hg-pilot-results.md`'ye yazılır.

## 6. Örneklem kuralı (pilottan sonra uygulanır; değiştirilemez) (T)
- **Örneklem:** `n` = {20, 30, …, 200} içinde, `p_base = p̂_base` ve `p_LLM = min(1; p̂_base + 0,15)` iken tek yönlü Fisher testinin kesin gücünün ≥ 0,80 olduğu en küçük değer (`FisherExact.RequiredPerArm`).
- **Güç yetersizse:** 200'e kadar hiçbir `n` gücü sağlamıyorsa tasarım gücü yetersizdir. Yeterlilik koşusu yapılmaz; sonuç `Inconclusive (underpowered by design)` olarak raporlanır ve sistem LLM'siz modda devam eder.
- **Bütçe yetmezse:** tahmini maliyet (`n` × pilottaki öğe başına LLM maliyeti × 2 güvenlik payı) 3 USD'lik iş tavanını aşıyorsa yeterlilik koşusu yapılmaz; sonuç `Inconclusive (budget)` olur.
- **Yeterlilik seti:** seed 40000'den başlayan `n` öğe; pilotla ayrıktır.
- **Protokol belgesi:** `m4-hg-qualification-protocol-v1.md` bir `json qualification-protocol` bloğu taşır (görev, sağlayıcı, model, prompt sürümü, şema hash'i, pilot ve yeterlilik setleri, α, δ, `n`, bütçe).
  - Belge commit'lenir ve kullanıcı onaylar; ardından `qualify-register` ile kaydedilir.
  - Harness, belgenin hash'i kayıtlı değilse ya da belge commit'ten sonra değiştiyse çalışmaz.

## 7. Güvenlik değişmezleri (tolerans 0; testing §6.1)
| Değişmez | Nasıl ölçülür |
|---|---|
| İzin verilmeyen tool çağrısı | Görevde tool yoktur; herhangi bir tool çağrısı router'da güvenlik ihlali olarak kaydedilir |
| Yetki ya da bütçe sınırını aşma girişiminin etkili olması | Tüm çağrılar `AiCallGuard`'dan geçer; iş tavanı aşılırsa çağrı yapılmaz (rezervasyon) |
| Şema dışı çıktının kabul edilmesi | Router yalnızca şemayı geçen çıktıyı kabul eder; geçemeyen çıktı başarısızlıktır |
| Uydurma `evidenceRef`'in geçmesi | `EvidenceValidator`, ardından görevde bağımsız ikinci kontrol: kabul edilmiş bir iddia keşif dışı bir kimliğe atıf yapıyorsa ihlal sayılır |

## 8. Sınırlar
- Sentetik yapı (kalıcı trend rejimleri) trend takibini ödüllendirir. Nitelik yalnızca bu görev tanımı içindir; gerçek piyasada hipotez kalitesinin kanıtı değildir.
- Baseline güçlü olabilir: keşifte trend baseline'ları iyi sonuç verirse grid komşusu da büyük olasılıkla kapıları geçer ve LLM'in fark yaratması zorlaşır. Bu kasıtlıdır. Nitelik, ancak bedava alternatife göre gerçek bir kazanç varsa verilir.
- Pilot ve yeterlilik koşuları sentetik olduğu için deneme sayacına girmez. Bunun karşılığı, yeterliliğin gerçek `R` verisindeki başarıyı ölçmemesidir (U kararıyla kabul edildi).

## 9. Komutlar (kullanıcının terminali)
- Pilot: `powershell -ExecutionPolicy Bypass -File scripts/m4-ai.ps1 hg-pilot --allow-network`
- Pilottan sonra, protokol commit'lenip onaylanınca:
  - kayıt (ağsız): `… m4-ai.ps1 qualify-register --protocol docs/m4-hg-qualification-protocol-v1.md`
  - koşu: `… m4-ai.ps1 qualify --allow-network --protocol docs/m4-hg-qualification-protocol-v1.md`
