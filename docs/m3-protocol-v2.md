# M3 Protokolü — Protokol v2 (ön kayıt, v1'e göre değişiklikler)

- Durum: **Ön kayıt — kullanıcı tarafından onaylandı (2026-10-16, U)**; §3'teki doğru pozitif tanımı, kullanıcının birim ve ulaşılabilirlik kontrolü talimatıyla yeniden yazıldı (§2a). Ön kayıt kanıtı, bu belgenin eklendiği git commit'idir. Bu commit'ten önce v2 kurallarıyla hiçbir kalibrasyon koşulmadı.
- Gerekçe: Protokol v1 kalibrasyonu (ön kayıt `9928531`) bir kez ve aynen koşuldu; hiçbir eşik bileşimi FP ≤ %5 ve TP ≥ %80 şartlarını birlikte sağlamadı (`m3-calibration-results.md`). v1 §7 gereği eşikler gevşetilmedi ve atanmadı. v1 ve sonuçları silinmez; bu belge yeni sürümdür.
- Kapsam: Yalnızca aşağıdaki maddeler değişir. Diğer her şey `m3-protocol.md` (v1) ile aynıdır: indikatörler, DSL, birincil metrik, B5 değerleri (D26), karar sırası, süre aracı, aile kuralları, Kohort 0 ve E1. Ön kaydın "v1" olarak geçtiği yerlerde, protokol v2 için de aynı kurallar geçerlidir.
- Karar türleri: **U** = kullanıcı, **T** = teknik lider.

## 1. Araştırma kapıları `no-halt` koşusunda (T, kullanıcı onaylı yön, 2026-10-16) — v1 §6'nın yerine
- G1–G6, `R` üzerinde **`no-halt` koşusuyla** değerlendirilir: maliyet modeli v1, D25 hard stop, günlük kayıp kuralı, pozisyon ve maruziyet tavanları aynen geçerlidir; yalnızca %15 drawdown halt'ı kapalıdır.
- Gerekçe: halt, portföy düzeyinde bir risk kuralıdır ve koşu sonuna kadar yeni girişi durdurur. v1 kalibrasyonunda gömülü kenarlı 50 koşunun 50'si halt'a girdi; halt sonrası yıllar sıfır getiri verdiği için G3 yapısal olarak başarısız oldu. Kapılar stratejinin kendisini ölçmelidir.
- `policy` koşusu da hesaplanır ve raporlanır; **ileriye dönük değerlendirme (v1 §4) değişmez:** `policy` koşusuyla yapılır. Drawdown kuralı (> 0,25 → `Failed`) aynen geçerlidir.

## 2. Deflated Sharpe'ta deneme varyansı (T) — v1 §6 G6'nın yerine
- Deneme Sharpe ratio'larının varyansı `V`, kayıtlı denemelerin ya da null dağılımının kesitsel varyansı yerine **Sharpe tahmincisinin null altındaki varyansıdır:** `V = 1 / (T − 1)`. Burada `T`, adayın 4h getiri gözlem sayısıdır.
- Gerekçe: null dağılımının kesitsel varyansı, maliyet nedeniyle derin negatif Sharpe'lı stratejilerle şişiyor ve beklenen maksimum Sharpe eşiğini anlamsız biçimde yükseltiyordu. Bağımsız null denemeler varsayımı altında, Bailey ve López de Prado'nun `V[SR]` terimi tahmin varyansıdır.
- Deneme sayısı `N`: araştırmada kesim dönemi boyunca global deneme sayacı (v1 ile aynı). **Kalibrasyonda `N = 100`** (v1 kalibrasyonunda uygulama seçimiydi; burada ön kayda alınır).

## 2a. Birimler ve teorik ulaşılabilirlik (T, kullanıcı talimatıyla)
- `SR̂` 4h dönem başına (ortalama/örneklem std); `V = 1/(T − 1)` aynı ölçekte (dönem başına), yıllıklandırma yok.
- `T = 8.766` için `√V = 0,01068`. `N = 100` için beklenen maksimum katsayısı `b(N) = (1 − γ)·Z(1 − 1/N) + γ·Z(1 − 1/(N·e)) ≈ 0,4228·2,326 + 0,5772·2,680 ≈ 2,53`; `SR₀ ≈ 0,0270`.
- DSR olasılığı ≈ `Φ(t − b(N))`, `t = SR̂·√(T − 1) = SR_yıllık·√4`. En gevşek eşik `τ_dsr = 0,90` bile `t ≥ 3,81` (yıllık Sharpe ≥ 1,9) ister.
- Yıllık ≈ %15 etki, v1'de ölçülen ≈ %20 yıllık oynaklıkla yıllık Sharpe ≈ 0,75, `t ≈ 1,5` eder. Bu etki G6'yı (ve G2'yi) teorik olarak geçemez. **Sonuç:** 4 yıllık pencerede `N = 100` deneme ile G6 yalnızca yıllık Sharpe'ı ≈ 2 ve üstü olan stratejileri geçirir. Bu bir tasarım sonucudur ve kabul edilir; yanlış pozitif kontrolü önceliklidir.

## 3. Kalibrasyon (T) — v1 §7'ye ekler
- Hedefler, aday eşik ızgarası, seçim kuralı, durma kuralı, null strateji üreteci seed'i (1), null evren seed'leri (10.000…) ve doğru pozitif evren seed'leri (20.000…) **v1 ile aynıdır.**
- **Gömülü kenarın ayarı (v1'deki "≈ %15 yıllık etki" hedefinin yerine):** doğru pozitif etki, en gevşek DSR eşiğinin %80 güçle yakalayabildiği en küçük etkidir. Hedef t-istatistiği `t* = b(100) + z(0,90) + z(0,80) ≈ 2,53 + 1,28 + 0,84 ≈ 4,65` (yıllık Sharpe ≈ 2,3). Gerekçe §2a: ≈ %15 etki G6'yı teorik olarak geçemez.
- κ seçimi: `κ ∈ {0,020; 0,021; …; 0,080}` ızgarasında, `tp-trend` stratejisinin 50 doğru pozitif evrendeki `no-halt` koşusunda ortalama `SR̂·√(T − 1)` değeri `t*`'a en yakın olan κ (eşitlikte küçük olan). Bu arama yalnızca doğru pozitif stratejinin Sharpe'ını kullanır; hiçbir kapı sonucuna bakmaz. v1'deki κ = 0,063 `policy` koşusuna göre ayarlanmıştı; geçerli değildir.
- Kalibrasyon bu sürümle **bir kez** koşulur. Sonuç ne olursa olsun `m3-calibration-results-v2.md`'ye yazılır. Şartlar yine sağlanmazsa eşikler gevşetilmez; durulur ve rapor sunulur.

## 4. Tanısal değerlendirme kuralı (U, 2026-10-16) — yeni bölüm
- Tanısal holdout `[C, A)` yalnızca eler (D10). Karar:
  - maksimum drawdown > 0,25 **ya da** birincil metriğin tek yönlü %95 üst sınırı < 0 ise → `Rejected`;
  - aksi halde → `NotRejected`.
- Koşu türü `policy`, maliyet modeli v1. Bootstrap ve üst sınır v1 §4'teki gibi hesaplanır: `B = 10.000`, seed tanım hash'inin ilk 8 hex karakterinden türetilir.
- Research yalnızca sonucu görür; metrikleri yalnızca kullanıcı görür (architecture §6.5).

## 5. Etkilenmeyenler
- Kohort 0 araştırma kapılarından muaftır (v1 §10); bu sürüm Kohort 0'ın içeriğini değiştirmez. Kohort 0 manifestosu protokol sürümü olarak `m3-protocol v2`'yi taşır.
- B5 değerleri (D26) ve karar sırası değişmez.
