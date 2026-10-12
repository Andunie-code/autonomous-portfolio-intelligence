# ADR-0006: Locked holdout, dönem ve kullanım politikası, deneme muhasebesi

- Durum: Kabul edildi · Tarih: 2026-10-12 · Revizyon: 2026-10-13 (rev. 3, teknik lider kararları T3–T6, T18)
- Uygulama durumu: **Planlandı, uygulanmadı**
- İlgili: ADR-0009 (güven sınırı ve topoloji)

## Bağlam
Otomatik hipotez üretimi çok sayıda deneme demektir. Holdout iki yoldan kirlenebilir:
1. Veri, geliştirme ve araştırma iş akışlarına sızabilir (izolasyon: ADR-0009).
2. Holdout tekrar tekrar sorgulanarak ona uydurma yapılabilir (bu ADR).

Veri kamuya açıktır. Amaç gizlilik değil, değerlendirmenin güvenilirliğidir.

## Karar

### Dönemler
- **1. dönem:** `H_end` = M1 başlangıç tarihi, `H_start` = `H_end − 9 ay` (D10). Dondurulur.
- **Sonraki dönemler yalnızca ileriye dönük açılır:** `[T, T + 9 ay]`. `T`'den itibaren bu pencere Research'ten gizlenir. Dönem, `T + 9 ay`'da değerlendirmeye açılır.
- Araştırmanın daha önce gördüğü bir geçmiş dönem asla holdout yapılmaz.
- Dönem açılışında kota **K** ve geçme **α**'sı kaydedilir. Değerleri TBD: beklenen aday sayısına göre seçilir.

### Aileler ve deneme muhasebesi
- `TrialFamily` = parametreleri çıkarılmış, normalize edilmiş DSL yapısının hash'i. Aileyi istemci değil, Registry ve evaluator **hesaplar**.
- Tüm denemeler (LLM, grid, insan, geliştirmede Claude Code destekli) sayaca girer ve Deflated Sharpe'ta kullanılır.

### Holdout kullanımı
- Bir aile, bir dönemde en fazla **bir kez** değerlendirilir.
- Dönem başına toplam değerlendirme sayısı ≤ K.
- Geçme kararı: önceden kayıtlı birincil test (maliyetler düşülmüş getirinin benchmark'a karşı block-bootstrap testi), dönem içindeki değerlendirme sayısına göre **Holm** düzeltmesiyle.
- **Açıklama:** geçti/kaldı ve 2 anlamlı basamağa yuvarlanmış özet metrikler. Zaman serisi, işlem listesi ya da mum verisi döndürülmez.
- **Sürüm:** Evaluator, adayın araştırma değerlendirmesiyle **aynı engine SHA**'sını çalıştırmak zorundadır. Her sonuç `evaluatorVersion` (image digest ve engine SHA), `epochId` ve holdout veri seti hash'ini taşır. Bir aileyi yeni sürümle yeniden değerlendirmek aynı dönem içinde yasaktır.
- **Ön koşul:** `HoldoutEnvironmentVerified = true` (H-ENV testleri). Bu olmadan `Candidate → HoldoutTested` geçişi kapalıdır.

### Veri sınırı (özet; ayrıntılar ADR-0009'da)
- Geliştirme makinesine pencere verisi indirilmez. Sınır ayı için günlük dosyalar kullanılır; dev DB'sinde `CHECK` kısıtı vardır.
- ops DB'sinde Research yalnızca pencereleri dışlayan `research.candles` view'ını okur.
- Holdout verisini yalnızca evaluator, doğrudan kaynaktan import eder.

## Gerekçe
- Aileyi yapı hash'iyle tanımlamak, parametre varyantlarıyla holdout'u "deneme yanılma" yoluyla kullanmayı engeller.
- Kota ve Holm düzeltmesi, yapısal varyantların çoklu test etkisini sınırlar.
- Yuvarlanmış özet yanıt, değerlendirme başına sızan bilgiyi azaltır.
- İleriye dönük dönem, yeniden kullanılan bir holdout'un kirlenmesini önleyen en basit yöntemdir.

## Sonuçlar ve kabul edilmiş riskler
- Holdout tükenebilir bir kaynaktır; yeni dönem 9 ay bekleme gerektirir. Bu sürede terfi kanıtı paper trading'den gelir.
- İleriye dönük pencere sırasında Research, mevcut stratejilerin paper sonuçlarını görür. Bu zayıf, dolaylı bir sızıntıdır ve kabul edilmiştir.
- Veri kamuya açıktır; doğrudan indirmeye karşı süreç kuralı ve audit uygulanır.
- Doğrulama: `testing-strategy.md` §7 (H1–H20).
