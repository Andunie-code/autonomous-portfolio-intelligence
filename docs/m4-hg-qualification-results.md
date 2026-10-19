# HypothesisGeneration yeterlilik sonucu (protokol v1)

> **Sonuç: `Failed`** (`TaskQualification` id 1). Ön kayıtlı karar kuralı aynen uygulandı. HypothesisGeneration için **nitelikli model yoktur**: görev politikasında `qualifiedModels` boş kalır, `onUnavailable = DeterministicFallback` geçerlidir ve araştırma LLM'siz modda (grid ve insan onaylı hipotezler) sürer (D20).

- **Protokol:** `m4-hg-qualification-protocol-v1.md`, commit `c434d84`; kayıtlı hash `89075de83cd6ffbfa267c4606c832f5a5b67cae3b6e75441fc30dc5b7e0832ed`. Tasarım `m4-hg-qualification-design.md` v1 (`1165601`); pilot `m4-hg-pilot-results.md`.
- **Koşu:** kullanıcının etkileşimli koşusu; kayıt zamanı 2026-10-19T00:10:15Z; iş `qualification-89075de83cd6`. 70 `DecisionTrace` kaydı var (mod `Qualification`).
- **Konfigürasyon:** model `gemini-3.5-flash-lite`, prompt `hg-synthetic-prompt v1`, şema `770469e8…`, seed 40000–40069 (70 öğe), sentetik. `research.trials`'a hiçbir şey yazılmadı; global deneme sayacı 98'de kaldı.
- **Maliyet:** 0,112708 USD (bütçe 1,00 USD; iş tavanı 3 USD). Kill switch tetiklenmedi.

## Birincil sonuç

| Kol | Başarı (G1–G6, değerlendirme evreni) | Oran |
|---|---|---|
| LLM | 22 / 70 | %31,4 |
| Baseline (deterministik grid) | 58 / 70 | %82,9 |

- Tek yönlü Fisher exact (H1: LLM > baseline): **p = 1,0000** (α = 0,05).
- Oran farkı −0,514; asgari etki +0,15.
- Güvenlik ihlali **0.**
- **Karar sırası:** güvenlik ihlali yok → örneklem tamamlandı (70/70) → `p ≥ α` → `Failed`.

## İkincil metrikler (betimleyici; karar değil)
- **Tanım üretilemeyen LLM yanıtı:** 34. Nedeni kanıt reddi ("evidence validator rejected the output"), pilottaki örüntünün aynısı (`m4-hg-pilot-results.md`).
- **Geçersiz DSL:** 4. Keşif kopyası: 0.
- **Geçerli tanım üreten yanıtlar:** 32. Bunların 22'si tüm kapıları geçti (%68,8). Bu koşullu oran da baseline'ın genel oranının (%82,9) altında kalıyor.
- **LLM'in geçip baseline'ın geçemediği öğeler:** 3 (`hg-40055`, `hg-40056`, `hg-40066`).
- Bu satırdaki dağılımlar, kullanıcının paylaştığı koşu JSON'undan alındı. Birincil sonuç `ai.task_qualifications` kaydıdır.

## Yorum (T)
1. **Süreç doğru işletildi** (roadmap M4 kriteri):
   - pilot, yeterlilik setinden ayrı;
   - protokol ve hash koşudan önce commit'lendi ve kaydedildi;
   - örneklem kesin güç analiziyle belirlendi;
   - sonuç `TaskQualification`'a yazıldı;
   - sapma yok.
2. **Model, bedava deterministik alternatifi geçemedi.**
   - Başarısızlığın yarısı biçimsel: kanıt alanının yanlış kullanılması.
   - Kalan yarı ise içerik farkı: geçerli tanımlarda %69 başarı, baseline'da %83.
   - Kanıt sorunu giderilse bile bu protokolde 0,15'lik üstünlüğe ulaşılması beklenmez.
3. **Bu sonuç yalnızca bu görev tanımı için geçerlidir** (sentetik öğeler, bu prompt, bu şema, bu model). Gerçek piyasadaki hipotez kalitesi hakkında bir şey söylemez.
4. **Gelecekte bir deneme ancak yeni bir protokolle mümkündür** (ADR-0010): yeni tasarım sürümü, yeni pilot, yeni setler. Örneğin metrik adını taşıyan bir iddia alanı ya da başka bir model. Bu protokolün sonucu silinmez ve değiştirilmez.
