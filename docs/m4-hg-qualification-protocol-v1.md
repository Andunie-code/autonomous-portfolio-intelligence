# HypothesisGeneration yeterlilik protokolü (v1)

- **Durum:** Ön kayıt — kullanıcı tarafından onaylandı (2026-10-19, U; ön kayıtlı kuraldan sapmadan v1 ile devam). Sıra:
  1. bu belge commit'lenir;
  2. `qualify-register` ile kaydedilir: belgenin SHA-256'sı `ai.qualification_protocols`'a yazılır ve commit'ten sonra değişen bir belgeyle harness çalışmaz;
  3. koşu **bir kez** yapılır.
- **Dayanak:** `m4-hg-qualification-design.md` v1 (ön kayıt `1165601`) ve `m4-hg-pilot-results.md`. Bu protokol yalnızca tasarımın §6 kuralını uygular; tasarımdaki hiçbir kuralı değiştirmez.
- Karar türü: **T**, kullanıcı onayıyla (harcama ve nitelik kararı).

## Sabitlenen değerler

| Alan | Değer |
|---|---|
| Görev sınıfı | HypothesisGeneration |
| Sağlayıcı ve model | `gemini` / `gemini-3.5-flash-lite` |
| Prompt sürümü | `hg-synthetic-prompt v1` |
| Çıktı şeması hash'i | `770469e889e9bea7ab11cc26e8d02621cfe1800f96afed2fe826c27b0d6ad1fa` |
| Öğe tanımı | `hg-synthetic v1` (κ = 0,056, ortalama rejim 300 mum, yukarı payı 0,5, değerlendirme seed farkı 500.000) |
| Pilot seti | seed 30000–30019 (20 öğe; set hash'i `e72eeccc71694dfc66cf75ff90fb688c931284ac5f3fb6933e4f337d74cc7efd`) |
| Yeterlilik seti | seed **40000–40069** (70 öğe; pilotla ayrık) |
| Kol başına örneklem | **70** (tasarım §6: p̂_base = 0,90 iken kesin güç 0,84) |
| Test | Tek yönlü Fisher exact (LLM > baseline), α = **0,05** |
| Asgari etki | δ = **0,15** (oranlar arası mutlak fark; kullanıcı onaylı) |
| Karar | Güvenlik ihlali → `Failed`; örneklem tamamlanmadı → `Inconclusive`; aksi halde `p < 0,05` ve fark ≥ 0,15 → `Passed`, değilse `Failed` |
| Bütçe | **1,00 USD** (tahmin ≈ 0,23 USD; iş tavanı 3 USD) |
| Başarı tanımı | Tasarım §3: değerlendirme evreninde G1–G6, v2 eşikleri, `no-halt`, DSR'de N = 100 |
| Deneme sayacı | Sentetik; `research.trials`'a yazılmaz (U, 2026-10-19) |

```json qualification-protocol
{
  "task": "HypothesisGeneration",
  "provider": "gemini",
  "model": "gemini-3.5-flash-lite",
  "promptVersion": "hg-synthetic-prompt v1",
  "schemaHash": "770469e889e9bea7ab11cc26e8d02621cfe1800f96afed2fe826c27b0d6ad1fa",
  "pilot": { "seedBase": 30000, "count": 20 },
  "qualification": { "seedBase": 40000, "count": 70 },
  "alpha": 0.05,
  "minimumEffect": 0.15,
  "samplePerArm": 70,
  "budgetUsd": 1.00
}
```

## Komutlar
1. Kayıt (ağsız; belge commit'lendikten sonra): `powershell -ExecutionPolicy Bypass -File scripts/m4-ai.ps1 qualify-register --protocol docs/m4-hg-qualification-protocol-v1.md`
2. Koşu (kullanıcının terminali; anahtar oturumda): `powershell -ExecutionPolicy Bypass -File scripts/m4-ai.ps1 qualify --allow-network --protocol docs/m4-hg-qualification-protocol-v1.md`

Sonuç, ne olursa olsun, `ai.task_qualifications`'a yazılır ve `docs/m4-hg-qualification-results.md`'de raporlanır. `Passed` çıkarsa bile `qualifiedModels` konfigürasyonuna giriş eklemek ayrı bir kullanıcı kararıdır (tasarım §4).
