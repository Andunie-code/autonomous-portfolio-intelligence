# HypothesisGeneration v2 pilot sonuçları ve döngünün sonucu

> **v2 döngüsünün sonucu: `Inconclusive (budget)`.** Ön kayıtlı bütçe kuralı (tasarım v2 §6) gereği yeterlilik koşusu **yapılmaz.** HypothesisGeneration'da nitelikli model yoktur; araştırma LLM'siz modda sürer (D20). v1 sonucu (`TaskQualification` 1, `Failed`) değişmez. v2 için `TaskQualification` kaydı yoktur, çünkü harness koşmadı. Bu belge ve CHANGELOG sonucun kaydıdır.

- **Pilot:** ön kayıt `13ad935` (`m4-hg-qualification-design-v2.md`, belge hash'i `0f2b043f7786…`). Kullanıcının etkileşimli koşusu, iş `hg-pilot-0f2b043f7786`.
- **Konfigürasyon:** model `gemini-3.8-flash`, prompt `hg-synthetic-prompt v2`, şema `f4e85d3d…`, öğe `hg-synthetic v2` (keşif `misfit-v2`).
- **Set:** seed 60000–60019 (set hash'i `91377be7…`). Sentetik; `research.trials`'a yazılmadı.
- **Pilot öncesi spike (`gemini-3.8-flash`):**
  - 1024 token sınırıyla 3/5 PASS. Başarısızlıklar spike ayarlarından kaynaklandı (CHANGELOG 2026-10-19).
  - 8192 token sınırıyla `dsl-in-neutral-schema` PASS (1128 çıktı token'ı, 10,1 sn).
  - `modelVersion` = `gemini-3.8-flash`.
- **Pilot maliyeti:** 0,510653 USD (bütçe 1,00 USD). Ekim ayı ledger toplamı 0,671812 USD.

## Pilot özeti

| Kol | Başarı (G1–G6, değerlendirme evreni) | Oran |
|---|---|---|
| LLM (`gemini-3.8-flash`) | 12 / 20 | %60,0 |
| Baseline (deterministik grid, misfit keşfi) | 11 / 20 | %55,0 |

- **Ayrışan öğeler:** LLM geçti ve baseline kaldı: 2; baseline geçti ve LLM kaldı: 1; ikisi de geçti: 10; ikisi de kaldı: 7.
- **Kanıt reddi:** 1 (v1'de 10/20). Geçersiz DSL 0, budanan indikatör 0, keşif kopyası 0, güvenlik ihlali 0.
- **Geçilemeyen kapılar:** LLM'de G4 7, G6 7, G2 3, G3 2, G1 1, G5 1; baseline'da G4 9, G6 8, G2 4, G3 4, G1 2, G5 2.
- **Baseline oranı:** pilotta %55, çevrimdışı kalibrasyonda %34 (50 öğe, ayrık seed'ler). Fark örneklem değişkenliğiyle uyumlu; bant hedefi kalibrasyon ölçümüyle doğrulanmıştı.

## Ön kayıtlı kuralların uygulanması (tasarım v2 §6; değiştirilmeden)
- **Örneklem:** `p_base = 0,55`, `p_LLM = 0,70` iken kesin Fisher gücü n = 140'ta 0,80'in hemen altında kalıyor (çıktıda yuvarlanmış 0,80); n = 150'de 0,83. Gerekli örneklem **n = 150**.
- **Öğe başına maliyet:** 0,510653 / 20 = **0,02553 USD.**
- **Tahmini maliyet:** 150 × 0,02553 × 2 = **7,66 USD > 5,00 USD** → kural: *yeterlilik koşusu yapılmaz; sonuç `Inconclusive`.*
- Kural pilottan önce, kullanıcının bütçe esnekliği dahil edilerek yazılmıştı (`13ad935`). Pilot sonucuna bakılarak değiştirilmez.

## Bulgular (betimleyici; nitelik değil)
1. **Düzeltmeler çalıştı:** isimli metrik alanıyla kanıt reddi 10/20'den 1/20'ye düştü. v1'deki biçimsel başarısızlığın kaynağı şemadaki belirsizlikti.
2. **Gözlenen fark küçük:** +0,05 (12/20 ile 11/20); asgari etki 0,15. Koşu yapılsaydı ve gerçek fark gözlenene yakın olsaydı, testin anlamlılık vermesi olası değildi. Kesin güç: gerçek oranlar 0,60 ve 0,55 iken n = 150'de **0,18**; buna karşılık 0,70 ve 0,55 iken n = 140'ta 0,796. Ayrıca anlamlılık tek başına yetmezdi: karar kuralı fark ≥ 0,15 de istiyor.
3. **Maliyet:** `gemini-3.8-flash` öğe başına `gemini-3.5-flash-lite`'ın (v1 pilotu, 0,00163 USD) yaklaşık 16 katı. Bunun ana nedeni thinking token'ları (S-G8).
