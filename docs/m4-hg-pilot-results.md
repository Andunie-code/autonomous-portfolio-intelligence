# HypothesisGeneration pilot sonuçları (m4-hg-qualification-design v1 §5)

- **Durum:** Pilot ön kayda göre (`1165601`) bir kez koşuldu. Pilot **nitelik vermez**; `TaskQualification` kaydı üretilmedi.
- **Koşu:** kullanıcının etkileşimli koşusu (iş `hg-pilot-270fe2bf4ac2`); tasarım belgesi hash'i `270fe2bf4ac2…`.
- **Konfigürasyon:** model `gemini-3.5-flash-lite`, prompt `hg-synthetic-prompt v1`, şema hash'i `770469e8…`.
- **Set:** seed 30000–30019 (set hash'i `e72eeccc…`).
- **Maliyet:** 0,032664 USD (öğe başına ≈ 0,00163 USD; 20 çağrı, şema onarım isteği yok). Sentetik veri; `research.trials`'a yazılmadı.

## Özet

| Kol | Başarı (G1–G6 değerlendirme evreninde) | Oran |
|---|---|---|
| LLM | 6 / 20 | %30,0 |
| Baseline (deterministik grid) | 18 / 20 | %90,0 |

- Güvenlik ihlali **0**, geçersiz DSL 0, keşif kopyası 0.
- Tanım üretilemeyen LLM yanıtı 10. Hepsinin nedeni "evidence validator rejected the output".
- Tanım üreten 10 LLM yanıtından 6'sı kapıları geçti. Geçemeyen 4'ünde G4 (4/4), G6 (3/4), G5 (1/4) ve G2 (1/4) başarısız.
- Baseline'ın geçemediği 2 öğede G4 ve G5 ya da G4 ve G6 başarısız. Baseline önerisi 18 öğede aynı tanım: `exp:1` (ema-trend) tanımının ilk parametre komşusu.

## Teşhis: kanıt reddi (T)
`ai.decision_traces` kayıtları (`m4-ai.ps1 traces --job hg-pilot-270fe2bf4ac2`), 10 reddin tamamında aynı örüntüyü gösteriyor. Model `claims[].value` alanına `M` (birincil metrik) yerine başka bir sayı yazmış:
- işlem sayısı (ör. 315, 334, 341, 348, 356, 367, 373);
- yıl sayısı (4);
- kârlı parite sayısı (13–18);
- maksimum drawdown (0,178; 0,335).

`EvidenceValidator` bu değerleri, atıf yapılan deneyin gösterilen `M` değeriyle karşılaştırıp **doğru biçimde** reddetti. Validator'da bir hata yok; uydurma bir referans da yok (bütün referanslar gerçek keşif kimlikleri).

Neden iki taraflı:
- **Model:** şemadaki açıklamaya ("the cited primary metric value") ve sistem talimatına ("a cited number must equal the value shown") uymadı.
- **Tasarım:** prompt her deney için birden fazla sayı gösteriyor, ama şema yalnızca tek ve adsız bir `value` alanı sunuyor. Bu, alanın yanlış kullanılmasını kolaylaştırıyor.

Ön kayıt gereği prompt, şema ve validator bu tasarım sürümünde değiştirilmez. Bulgu, gelecekteki bir v2 tasarımı için girdidir: metrik adını taşıyan bir iddia alanı (`metric` enum ve `value`), validator'ın da adı geçen metriği kontrol etmesi. Bir v2, yeni bir pilot ve yeni setler gerektirir.

## Örneklem kuralının uygulanması (tasarım §6; değiştirilmeden)
- `p_base = p̂_base = 0,90`, `p_LLM = min(1; 0,90 + 0,15) = 1,00`, tek yönlü Fisher, α = 0,05.
- **Kesin güç:** n = 60 → 0,73; n = 70 → **0,84**; n = 80 → 0,91.
- **Gerekli örneklem:** kol başına **n = 70**.
- **Bütçe kontrolü:** 70 × 0,00163 × 2 ≈ **0,23 USD** ≤ 3 USD (iş tavanı) → kural yeterlilik koşusuna izin veriyor.
- **Not:** baseline oranı yüksek olduğu için kuraldaki alternatif `p_LLM = 1,00`'a kırpılıyor. Güç, "LLM her öğede başarılı" senaryosuna göre hesaplanıyor. Kural ön kayıtlı olduğu için olduğu gibi uygulandı.

## Beklenti (betimleyici; karar değil)
- Pilotta LLM oranı (%30), baseline oranının (%90) çok altında. Kanıt reddi hiç olmasaydı bile, tanım üreten yanıtlardaki oran (6/10) baseline'ın altında kalırdı.
- Bu nedenle yeterlilik koşusunun ön kayıtlı karar kuralıyla **`Failed`** vermesi beklenir. Bu, M4 kabul kriterini bozmaz (roadmap: "Nitelik kazanılması kabul kriteri değildir; sürecin doğru işletilmesi kriterdir").
