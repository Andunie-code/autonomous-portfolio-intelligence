# HypothesisGeneration yeterliliği — tasarım ve pilot ön kaydı (v2)

- **Durum:** Ön kayıt — kullanıcı tarafından onaylandı (2026-10-19, U), dört düzeltme ve §6'daki bütçe kuralı dahil. Pilot ön kaydının kanıtı, bu belgenin eklendiği git commit'idir; pilot o commit'ten sonra koşulur. Bu belge yeterlilik protokolü değildir: örneklem büyüklüğü, seed'ler ve bütçe pilottan sonra `m4-hg-qualification-protocol-v2.md`'de, bu belgedeki kurallarla sabitlenir.
- **v1 ile ilişki:** v1 tasarımı, pilotu, protokolü ve sonucu (`TaskQualification` 1, `Failed`) değiştirilmez ve silinmez; tarihsel kayıttır (ADR-0010). v2, yeni bir tasarım, yeni bir pilot seti ve yeni bir yeterlilik setidir. Bu belgede yazılmayan her kural v1 (`m4-hg-qualification-design.md`) ile aynıdır.
- **Karar (U, 2026-10-19):** v2 döngüsü; model `gemini-3.8-flash`; pilot bütçesi 1,00 USD, yeterlilik bütçesi 3,00 USD; kanıt ve şemanın netleştirilmesi; kullanılmayan indikatörlerin budanması; baseline tavan etkisinin giderilmesi.

## 1. v1'den değişenler

| # | v1'deki sorun (`m4-hg-pilot-results.md`, `m4-hg-qualification-results.md`) | v2 düzeltmesi |
|---|---|---|
| 1 | Model `gemini-3.5-flash-lite` | **`gemini-3.8-flash`** (U). Fiyat S-G9: 0,75 $ / 3,75 $ (1M token; 2026 sonuna kadar) |
| 2 | 34/70 kanıt reddi: model, adsız `value` alanına işlem sayısı ya da parite sayısı gibi değerler yazdı | İddia şemasına **`metric`** alanı (enum: `primaryMetric`, `trades`, `positiveYears`, `profitablePairs`, `maxDrawdown`); validator **adı geçen metriği** kontrol eder; prompt'a kural satırı (prompt `hg-synthetic-prompt v2`, şema hash'i `f4e85d3d…`) |
| 3 | 4/70 geçersiz DSL ("declared but never used") | Prompt'ta kural satırı. Modelin tanımında hiçbir koşulun ya da stop'un referans vermediği indikatörler, katı parser'dan **önce budanır** ve kaydedilir. Bu davranışı değiştirmez; ADR-0016 ve parser değişmez |
| 4 | Tavan etkisi: keşifte iyi yapılandırılmış trend stratejileri vardı, grid baseline'ı %83–90 başarı aldı | Keşif kümesinde gömülü (trend) ailesinin **iyi yapılandırılmış hiçbir üyesi yok** (§2). Baseline oranı ayrı kalibrasyon seed'lerinde ölçüldü (§4) |

**Kasıtlı olarak seçilmeyen yol (T):** "validator, deney kaydındaki herhangi bir metrikle eşleşirse kabul etsin" yaklaşımı kullanılmadı. Bu yol, rastlantısal bir eşleşmeyi de geçirir ve kanıt kontrolünü zayıflatır. İsimli metrik, kontrolü aynı sıkılıkta tutar.

## 2. Öğe: `hg-synthetic v2` (T)
- **Değerlendirme:** evrenler v1 ile aynı: keşif `Trending(s, κ = 0,056, 300, 0,5)`, değerlendirme `Trending(s + 500.000, …)`.
- **Keşif kümesi (6 deney):**
  - `exp:1` RSI(14) ortalamaya dönüş (`rsi < 30` → `rsi > 55`, %5 stop);
  - `exp:2` Bollinger(20, 2) ortalamaya dönüş (`close < lower` → `close > middle`, %5 stop);
  - `exp:3` EMA 3/5 kesişimi, %1 stop: trend ailesi, ama trendi taşıyamayacak biçimde yapılandırılmış;
  - `exp:4` Donchian(5) kırılımı, Donchian(3) çıkışı, %1 stop: aynı gerekçe;
  - `exp:5`–`exp:6`: `RandomStrategies(s)` ile çekilen iki rastgele DSL stratejisi.
- Kurgu, herhangi bir LLM koşusundan önce sabitlendi ve kodda `HypothesisGenerationTask.MisfitExploration` olarak duruyor. ±%10'luk bir parametre komşusu, ilk dört deneyin kusurunu ortadan kaldırmaz.
- Öğe içeriği: `hg-synthetic v2; seed=…; kappa=0.056; meanBars=300; upShare=0.5; evalSeedOffset=500000; exploration=misfit-v2`. Öğe kimlikleri `hg2-<seed>`.

## 3. Kollar, başarı ve karar kuralı
- **LLM kolu:** model `gemini-3.8-flash`, prompt `hg-synthetic-prompt v2`, şema hash'i `f4e85d3de854ca9444229613d5067bcd8bf72511268d1c0d9e8640ab905f07e4`; tool yok, `maxOutputTokens` 8192 (thinking dahil, S-G8), timeout 120 sn.
- **Kanıt:** referanslar keşif kimlikleri olmalı. İsimli metrik, gösterilen değere göreli %0,5 içinde olmalı; `maxDrawdown` 3, `primaryMetric` 6 ondalıkla gösterilir. Referanssız kritik iddia reddedilir.
- **Budama:** budanan indikatörler kayıtta tutulur (`PrunedIndicators`). Budama sonrası tanım geçersizse başarısızlık sayılır.
- **Baseline kolu, başarı tanımı, test ve karar kuralı:** v1 ile aynı.
  - Baseline: en iyi keşif tanımının ilk parametre komşusu.
  - Başarı: değerlendirme evreninde G1–G6, v2 eşikleri, `no-halt`, DSR'de N = 100.
  - Test: tek yönlü Fisher, α = 0,05, hedef güç 0,80, **δ = 0,15**.
  - Güvenlik ihlali → `Failed`; eksik örneklem → `Inconclusive`.
- **Güvenlik değişmezleri:** v1 §7 ile aynı. Bağımsız ikinci kontrol, isimli metrik iddialarını da kapsar.

## 4. Baseline bandının kalibrasyonu (çevrimdışı; API çağrısı yok) (T)
- **Amaç:** v2 kurgusunda grid baseline'ının tavan etkisi göstermediğini, pilottan önce ve LLM sonucuna bakmadan doğrulamak. Kullanıcının hedef bandı **%10–40**.
- **Seed'ler:** 50000–50049 (50 öğe). Bu seed'ler yalnızca kalibrasyon içindir; pilot ya da yeterlilik setinde kullanılmaz.
- **Komut:** `AiCli hg-baseline --variant 2 --seed-base 50000 --count 50` (ağsız, veritabanısız, modelsiz).
- **Sonuç (2026-10-19, ilk ve tek deneme):** **17/50 = %34,0** → bant içinde. Kurguda bu ölçümden sonra bir değişiklik yapılmadı.
  - En sık geçemeyen kapılar: G4 (33), G6 (31), G2 (22).
  - Baseline önerileri çoğunlukla iki rastgele deneyden türüyor.

## 5. Pilot (ön kayıt; nitelik vermez)
- **Set:** seed 60000–60019 (20 öğe; set hash'i `91377be7867ab5b236703d37ac695b614bd9d823d2655fd12c24486132117920`); v1 setleri ve kalibrasyon seed'leriyle ayrık.
- **Bütçe:** 1,00 USD (U). Çağrı başına rezervasyon (8192 çıktı ve ≈ 3.000 girdi token'ı) ≈ 0,033 USD, çağrı başına tavanın (0,10 USD) altında; tavan değiştirilmedi.
- **Ölçülenler:** v1 §5'tekiler, ayrıca budanan indikatör sayısı ve kanıt reddi sayısı.
- Pilot bir kez koşulur; sonuçlar `docs/m4-hg-pilot-v2-results.md`'ye yazılır.

```json hg-pilot
{
  "variant": 2,
  "model": "gemini-3.8-flash",
  "pilot": { "seedBase": 60000, "count": 20 },
  "budgetUsd": 1.00
}
```

## 6. Örneklem kuralı (pilottan sonra; değiştirilemez)
- **Örneklem:** v1 §6 ile aynı: `n` ∈ {20, …, 200}; `p_base = p̂_base`, `p_LLM = min(1; p̂_base + 0,15)` iken kesin Fisher gücü ≥ 0,80.
- **Bütçe kuralı (U, pilottan önce, 2026-10-19):**
  - **Tahmini maliyet** = `n` × pilottaki öğe başına LLM maliyeti × 2.
  - **Tahmin ≤ 3,00 USD:** iş bütçesi **3,00 USD**.
  - **3,00 USD < tahmin ≤ 5,00 USD:** iş bütçesi **5,00 USD**. Koşudan önce konfigürasyondaki yeterlilik iş payı 0,30'dan 0,50'ye çıkarılır (aylık 10 USD tavanının yarısı; D4 içinde, teknik değişiklik, CHANGELOG'a yazılır).
  - **Tahmin > 5,00 USD ya da 200'e kadar hiçbir `n` gücü sağlamıyor:** yeterlilik koşusu yapılmaz; sonuç `Inconclusive` olur.
  - Kural pilot sonucuna göre değiştirilmez.
- **Yeterlilik seti:** seed 70000'den başlayan `n` öğe.
- **Not (betimleyici):** `p̂_base` ≈ 0,34 olursa gerekli `n` kabaca 130–150'dir. `gemini-3.8-flash`'ın öğe başına maliyeti pilotta ölçülür; bütçe kuralının sınırına yaklaşılabilir.

## 7. Geçerlilik sınırları (T)
- **Ölçülen şey:** v2, "mevcut deneyler yanlış ailede ya da kötü yapılandırılmışken yapısal bir sıçrama yapabilme" yeteneğini ölçer. Baseline kasıtlı olarak yerel bir aramadır.
- **Grid'in tamamıyla kıyas değildir:** sistemin gerçek LLM'siz modunda grid, insan onaylı geniş şablonları da kapsar (`m4-protocol.md`); v2 bu bütünle bir karşılaştırma yapmaz.
- **`Passed`'in anlamı:** yalnızca bu görev tanımı için geçerlidir (sentetik öğeler, prompt v2, şema v2, `gemini-3.8-flash`). `qualifiedModels`'e giriş eklemek ve gerçek araştırma hattında kullanmak ayrı bir kullanıcı kararıdır (v1 §4).
- **Band kalibrasyonu:** baseline bandı kullanıcının belirlediği bir hedeftir; LLM'in geçmesini kolaylaştırmak için değil, tavan etkisini gidermek için konmuştur. Ölçüm, kurgu sabitlendikten sonra, bir kez ve ayrık seed'lerde yapıldı (§4).
- Sentetik koşular `research.trials`'a yazılmaz; global sayaç 98'de kalır.

## 8. Komutlar (kullanıcının terminali)
1. **Model sürümü kontrolü (spike, ≈ 0,01 USD):** `powershell -ExecutionPolicy Bypass -File scripts/m4-ai.ps1 spike --allow-network --model gemini-3.8-flash`. `modelVersion`, `gemini-3.8-flash` ile aynı olmalı; değilse pilottaki her yanıt degraded sayılır.
2. **Pilot:** `powershell -ExecutionPolicy Bypass -File scripts/m4-ai.ps1 hg-pilot --allow-network --design docs/m4-hg-qualification-design-v2.md`. Model ve bütçe bu belgedeki bloktan okunur.
