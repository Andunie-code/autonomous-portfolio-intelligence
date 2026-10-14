# M1 Veri Politikası (v1.2)

- Durum: **Kullanıcı onaylı.** v1.0 (2026-10-14) onayı commit `025f0e2`'ye bağlıdır. **v1.1 (2026-10-14, U):** S3 listeleme host'unun eklenmesi (§3 madde 2); onayı bu sürümün eklendiği commit'e bağlıdır. **v1.2 (2026-10-14, U):** mevcut `close_time` kuralı §6'ya yazıya geçirildi ve Aralık 2021 ısınma dosyalarının ele alınışı belirlendi (Seçenek A); kod davranışı ve M1 sonucu (snapshot `a6c4e89b…`) değişmedi; onayı bu sürümün eklendiği commit'e bağlıdır. Bağlama yöntemi: commit SHA. Değişiklik yeni sürüm ve yeni onay gerektirir; eski sürüm git geçmişinde kalır.
- Kaynak: konuşmadaki v0.4/v0.5 taslakları, Opus denetimleri ve kullanıcının 2026-10-14 kararları.
- İlgili: ADR-0006, ADR-0012, ADR-0013, `requirements.md` (D10, D22, T4, T6, B4), `roadmap.md` M1, `testing-strategy.md` §2 ve §7.1.
- Etiketler: **U** = kullanıcı kararı; **T** = teknik karar (gerekçesiyle; teknik gerekçeyle revize edilebilir, seçim sonucunu değiştiriyorsa kullanıcıya sorulur).

## 1. Zaman tanımları
| Sembol | Değer | Anlam |
|---|---|---|
| `C` | `2026-01-01T00:00:00Z` | Araştırma kesimi. Tek doğruluk kaynağı: kod sabiti ve migration'daki SQL fonksiyonu; eşitlikleri test edilir (H1d) |
| `U` | `2022-01-01T00:00:00Z` | Evren ve sıralama tarihi (yalnızca bu belgedeki anlamıyla; `U0` ve "U kararı" etiketinden farklıdır) |
| `first_open_arşiv` | Arşivdeki ilk 1h mumun `open_time` değeri | ADR-0012'deki `first_open`. Gerçek listeleme ya da ilk işlem tarihi olduğu **iddia edilmez** |
| `U0` | `first_open_arşiv + 30 gün` | Listeleme sonrası karantina (ADR-0012) |
| Sıralama penceresi `W` | `[2021-07-05T00:00Z, 2022-01-01T00:00Z)` | 180 adet 1d mum (açılışı 2021-07-05 … 2021-12-31) |
| Araştırma penceresi `R` | `[U, C)` | 1461 gün; 35.064 adet 1h, 8.766 adet 4h mum |

- Tüm zamanlar UTC'dir, aralıklar yarı açıktır. Mum üyeliği: `S ≤ o` ve `o + Δ ≤ E` (T4). 1M mumunun süresi takvim ayıdır ve UTC'de hesaplanır.

## 2. Veri erişim matrisi (U)
| Veri | Kullanım | Not |
|---|---|---|
| `< U` | Yalnızca çekirdek parite sıralaması ve seçimi ile indikatör ısınması (warm-up) | Isınma aralığı: çekirdek pariteler için `W` ile aynı aralıkta 1h ve 4h. Isınma verisine kalite kapısı uygulanmaz, kapsamı raporlanır |
| `[U, C)` | Araştırma ve backtest verisi; çekirdek kalite kapısı | Sıralama dondurulduktan sonra edinilir (§5) |
| `≥ C` | **M1'de listelenmez, indirilmez, DB'ye giremez** | Dosya planlayıcı, istek doğrulayıcı, ayrıştırıcı ve `market.candles` `CHECK`'i |

## 3. Ağ ve sızıntı korumaları (U, T)
1. **Kapı 2 olmadan ağ yok (U):** Tüm M1 kodu ve testleri çevrimdışıdır (sahte zip ve sahte HTTP handler). Gerçek ağ erişimi yalnızca Kapı 2 onayından sonra, kullanıcının kendi terminalinde çalıştırdığı komutla yapılır. Gerçek mod; açık bir komut satırı bayrağı ve etkileşimli bir onay gerektirir (girdi yönlendirilmişse reddeder). Bayrak, `.claude/settings.json`'da Claude Code için deny kuralıyla engellenir (T).
2. **Host allowlist (U, v1.1):** her host'un tek bir rolü vardır.
   - `data.binance.vision`: yalnızca aylık kline dosyaları (zip ve `.CHECKSUM`). Bu host listeleme sorgularına S3 XML değil HTML web sayfası döndürür (2026-10-14 Kapı 2 denemesinde gözlendi; hiçbir veri yazılmadan koşu durdu).
   - `s3-ap-northeast-1.amazonaws.com`: yalnızca `data.binance.vision` kovasının path-style S3 listeleme sorguları (`GET /data.binance.vision?prefix=...`); yanıt S3 `ListBucketResult` olmalıdır (namespace `http://s3.amazonaws.com/doc/2006-03-01/`), değilse koşu durur.
   - Başka bir host eklenmesi yeni sürüm ve kullanıcı onayı gerektirir.
3. **Yönlendirme (U):** beklenmeyen her HTTP 3xx yanıtı reddedilir; otomatik yönlendirme kapalıdır.
4. **İstek doğrulama (T):** her istek, planlayıcıdan bağımsız bir doğrulayıcıdan geçer: host, yol öneki, izinli sorgu parametreleri ve tarih. Tarih içeren bir dosya ya da önek, `C`'yi aşan bir döneme dokunuyorsa ağ çağrısından önce reddedilir (H2).
5. **Yanıt doğrulama (T):** listeleme yanıtında dönen her anahtar, istenen önek ve izinli yıl aralığıyla uyuşmalıdır; uyuşmazlıkta işlem durur.
6. **Tek ağ çıkış noktası (T):** arşive giden tüm istekler aynı doğrulayıcı handler'dan geçer.
7. **Ham veri konumu (U):** ham arşiv dosyaları (zip, CSV, checksum) git deposuna ve OneDrive'a girmez. Varsayılan önbellek dizini `%LOCALAPPDATA%\PortfolioIntelligence\archive-cache`'tir; repo dizininin ya da OneDrive dizininin içindeki bir önbellek yolu reddedilir. `.gitignore` `data/`, `*.zip` ve `*.csv`'yi dışlar.
8. **Claude Code bağlamı (U):** ham mum verisi Claude Code bağlamına girmez. Claude Code yalnızca doğrulama ve kabul raporlarını, özet istatistikleri, şema ve test çıktılarını okur. Komut çıktısı ve loglar mum satırı içermez; ham listeleme yanıtı diske ya da loga yazılmaz.
9. **Kontaminasyon olayı (T):** yasak bir mum (`o + Δ > C`) ya da `C` sonrasına ait bir anahtar görülürse ilgili dosya reddedilir, indirilen bayt silinir, aşama durur ve olay (yalnızca meta veri) önbellek dizinindeki eklemeli olay günlüğüne ve rapora yazılır. Devam etmek kullanıcı onayı gerektirir.

## 4. Aday keşfi (U, T)
- **Kaynak (U):** aday listesi arşivden deterministik olarak çıkarılır; elle verilmez.
- **Yöntem (T):** sembol düzeyinde listeleme (`…/spot/monthly/klines/`, yalnızca dizin adları); adı `USDT` ile biten semboller tutulur. Her sembol için 1h önekinde yıl önekli (2017–2021) sorgularla ilk ay bulunur; 2022 ve sonrası önekler çekirdek aşamasında sorgulanmaz.
- İlk ay ≤ 2021-06 ise `n_exp = 180`. İlk ay 2021-07…2021-12 ise o ayın 1h dosyasındaki en küçük `open_time` okunur.
- **Çekirdek havuzu:** `first_open_arşiv ≤ 2021-12-02T00:00Z` (yani `U0 ≤ U`).
- **Sınırlama:** arşivin delist edilmiş sembolleri tutup tutmadığı doğrulanmadı. Havuz, bugünkü arşivde görünen sembollerle sınırlıdır ve `U` tarihindeki tarihsel evrenin tamamı olduğu iddia edilmez. Bu sınırlama hayatta kalma yanlılığı raporuna yazılır.

## 5. Çekirdek parite seçim prosedürü (U)
Adımlar sırayla ve her adımın kuralları bir sonraki adımın verisi görülmeden sabit olarak uygulanır:

1. **Dışlama (U):** aşağıdaki kategoriler çekirdekten kesin olarak dışlanır:
   - stablecoin'ler;
   - fiat taban varlıklar ve fiat-endeksli tokenlar;
   - kaldıraçlı tokenlar (`UP`, `DOWN`, `BULL`, `BEAR`);
   - sarmalanmış ve türev tokenlar (`WBTC`, `WBETH` vb.);
   - emtia ve altın tokenları (`PAXG`, `XAUT` vb.).

   Liste sürümlü bir yapılandırma dosyasıdır. Kaldıraçlı desen yalnızca, ek çıkarıldığında kalan ad aynı listede başka bir taban varlıksa uygulanır (T); belirsiz kalan adlar `REVIEW-PENDING` olarak işaretlenir ve çekirdekten dışlanır, raporda listelenir (T).
2. **Sıralama verisi:** `W` penceresindeki 1d mumlar.
3. **Sıralanabilirlik (U):**
   - `n_obs ≥ 90` (gözlenen geçerli gün sayısı) **ve**
   - `n_unk / n_exp ≤ 0,10` (UNKNOWN gün oranı).
   - `n_exp`: `first_open_arşiv` gününden itibaren `W` içindeki gün sayısı. UNKNOWN: beklenen ama arşivde mumu olmayan gün; **asla sıfır hacim sayılmaz.**
   - Koşulu sağlamayan semboller `UNRANKED` olarak nedeniyle listelenir; çekirdeğe girmez.
4. **Ölçüt (U):** gözlenen günlerin USDT quote hacminin **medyanı** (çift sayıda gözlemde ortadaki iki değerin ortalaması). Azalan sıralanır; eşitlikte sembol adı ordinal (kültürden bağımsız) artan (T).
5. **Kalite kapısı ve tamamlama (U):** sıralama dondurulduktan sonra, sırayla her adayın `R` aralığındaki 1h ve 4h verisi edinilir ve §6 kalite şartlarına bakılır. Delist olan ya da kalite şartını sağlamayan aday elenir, sıradaki aday geçer; **20 çekirdek parite** tamamlanana kadar devam edilir.
6. **20'ye ulaşılamazsa (U):** eşikler sessizce gevşetilmez; rapor yazılır ve durulur.
7. **Hayatta kalma yanlılığı raporu (U):** elenen tüm pariteler sıraları ve nedenleriyle listelenir. Etiketler: `ENDED` (sondaki boşluk), `LATE_START`, `INTERNAL_GAP`, `LOW_COVERAGE`, `INTEGRITY`. Çekirdek, C'ye kadar hayatta kalma koşuluyla seçildiği için **survivor-conditioned** olarak etiketlenir (`core-4y-S`); hayatta kalma yanlılığının giderildiği iddia edilmez.
8. **Kesinleşme:** pipeline'ın ürettiği liste bir **önerdir.** Çekirdek 20 parite, M1 kabul raporunun kullanıcı tarafından onaylanmasıyla kesinleşir; M2 bu onaydan önce başlamaz.

**Teknik varsayılanlar (T; seçim sonucunu etkileyebilir, Kapı 2'den önce kullanıcı itiraz edebilir):**
- Sıralama penceresinde listeleme sonrası ilk 30 gün (karantina) hariç tutulmaz.
- `n_exp`'in çapası 1h arşivindeki `first_open_arşiv`'dır.
- Tarama sınırı yoktur: sıralı listenin tamamı, 20 tamamlanana kadar taranır.

## 6. Veri kalitesi kuralları (U)
| Kural | 1h | 4h |
|---|---|---|
| Beklenen mum (`R`) | 35.064 | 8.766 |
| ≥ %99 mevcut | eksik ≤ 350 | eksik ≤ 87 |
| Tek boşluk ≤ 72 saat | ≤ 72 ardışık eksik mum | ≤ 18 ardışık eksik mum |

- Pencerenin başına ya da sonuna değen eksik koşu da boşluk sayılır. Payda sabittir. Eksik mumlar doldurulmaz (ileri taşıma ya da interpolasyon yok).
- ADR-0012'deki "kayıtlı ve piyasa genelindeki kesintiler hariç" muafiyeti **uygulanmaz**; eşikler katı uygulanır. Eşzamanlı eksiklik yalnızca raporda bilgi olarak gösterilir (T).
- **Bütünlük (U):** `.CHECKSUM` (SHA-256) doğrulaması zorunludur. Checksum'ı uyuşmayan ya da checksum dosyası olmayan dosya sessizce atlanmaz; hata verir ve reddedilir.
- **Bütünlük hatası sayılanlar (T; v1.2'de `close_time` kuralı yazıya geçirildi):** ayrıştırma hatası; OHLC kuralı ihlali (`Low ≤ min(Open, Close)`, `max(Open, Close) ≤ High`, fiyat > 0, hacim ≥ 0); yinelenen ya da azalan `open_time`; Δ ızgarasına oturmayan `open_time`; dosya adındaki ayın dışında kalan satır; ms/µs birim uyuşmazlığı; **`close_time` tutarsızlığı** (`open_time < close_time < open_time + Δ` sağlanmıyorsa). Böyle bir dosya bütünüyle reddedilir ve hiçbir satırı yazılmaz; sonuçta oluşan eksik mumlar kalite kapısına yansır ve sembol `INTEGRITY` etiketi alır.
- **Isınma aralığında reddedilen dosyalar (U, v1.2, Seçenek A):** `W` (ısınma) aralığında reddedilen bir dosya M1 sonucunu değiştirmez; o ayın verisi eksik kalır ve doldurulmaz. M1'de bu durum SOLUSDT, TRXUSDT ve SANDUSDT'nin Aralık 2021 1h dosyalarında oluştu (`close_time` kuralı). M2'den önce tüm paritelere aynı uygulanacak tek bir ısınma kuralı ön kayıt edilir: indikatörler veri boşluğunu atlamaz; bir parite, en uzun lookback'i geçerli mumlarla dolduğunda işleme başlar.
- **1h/4h tutarlılığı (U):** bir 4h mum, aynı aralıktaki dört 1h mumun hepsi varsa onlarla karşılaştırılır (open, high, low, close, taban hacim, quote hacim; ondalık tam eşitlik). Uyuşmazlık kabul raporunda **anomali** olarak işaretlenir; veri sessizce düzeltilmez ve seçim otomatik olarak değişmez; karar kullanıcıya raporlanır. 1h mumlarından biri eksikse blok "doğrulanamaz" olarak sayılır; bu bir hata değildir.

## 7. Kesim tarihi ve planlayıcı (T)
- **Dosya düzeyi:** planlayıcı yalnızca **tüm** mumları `o + Δ ≤ C` koşulunu sağlayan aylık dosyaları planlar. Örnek: 1h, 4h, 1d ve 1M için Aralık 2025 dosyası planlanır; 1w için Aralık 2025 dosyası 2025-12-29 açılışlı mumu içerdiği için planlanmaz.
- **Mum düzeyi:** ayrıştırıcı her satırı kontrol eder; yasak bir mumda dosyanın tamamı reddedilir (§3 madde 9). Satır bazında filtreleme yapılmaz.
- **Zaman birimi:** 13 basamak ms, 16 basamak µs; dosya ayı 2025-01 ve sonrası ise µs, öncesi ise ms beklenir (S-B7). Uyuşmazlıkta ret.
- **Son katman:** `market.candles` üzerinde `CHECK (open_time + aralık ≤ C)`, aralık UTC'de hesaplanır.

## 8. E1 ön aday listesi (T)
M1'de E1 adayları yalnızca **listelenir**, verileri import edilmez (roadmap M1). Ön aday: `first_open_arşiv > 2021-12-02T00:00Z` ve `U0 ≤ 2025-01-01T00:00Z`; bulunması için 2022–2024 yıl önekleri sorgulanır, 2025 ve sonrası sorgulanmaz. Kalite şartlarını sağlamayan çekirdek adayları E1'e taşınmaz (ADR-0012). Çekirdek şartlarını sağlayıp 20'ye seçilmeyen semboller E1 sayılmaz; raporda "çekirdek-yedek" olarak listelenir.

## 9. Kapılar (U)
| Kapı | Kapsam | Koşul |
|---|---|---|
| **Kapı 1** (açık) | Bu politika; M1'in tüm çevrimdışı kodu, migration'ları, birim, entegrasyon ve H-DEV (H1–H4) testleri | `scripts/verify.ps1` yeşil |
| **Kapı 2** | Gerçek arşiv erişimi (§3 madde 2'deki iki host): önce plan (ağsız), ardından gerçek listeleme ve import. Kullanıcı kendi terminalinde çalıştırır: `powershell -ExecutionPolicy Bypass -File scripts/m1-import.ps1` ve `… -AllowNetwork` | Kapı 1 tamam; kullanıcının tek aşamalı etkileşimli onayı |
| M1 kabulü | Kabul raporu (kapsam, boşluklar, hash, H1, hayatta kalma yanlılığı, anomaliler, önerilen çekirdek liste) | Kullanıcı onayı; çekirdek 20 parite bu onayla kesinleşir |

## 10. Tekrar üretilebilirlik (T)
- Her koşu bir `DatasetSnapshot` üretir: politika sürümü ve commit SHA, kaynak dosyalar (anahtar, boyut, SHA-256), aday havuzu (filtrelenmiş), dışlama ve sınıflandırma sürümü, sıralama girdileri ve çıktısı, kalite etiketleri, anomali raporu.
- Ham listeleme yanıtı saklanmaz; yalnızca sayılar ve özet saklanır.
- Aynı dosya kümesiyle yapılan ikinci koşu aynı snapshot hash'ini üretir. Arşiv bir dosyayı değiştirirse (farklı checksum) koşu farkı raporlar ve kanonik koşuyu kendiliğinden değiştirmez.
- 1d sıralama verisi de `market.candles`'a (aralık `1d`) yazılır; aynı `CHECK` koruması uygulanır.
