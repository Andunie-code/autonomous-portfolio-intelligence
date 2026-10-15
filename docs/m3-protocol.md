# M3 Protokolü — Protokol v1 (ön kayıt)

- Durum: **Ön kayıt — kullanıcı tarafından onaylandı (Kapı A, 2026-10-16).** Ön kayıt kanıtı, bu belgenin eklendiği git commit'idir. Bu commit'ten önce araştırma penceresi `R` üzerinde yeni bir backtest, kalibrasyon ya da parametre denemesi **çalıştırılmadı.** Kural değişikliği yeni sürüm (v2) demektir; v1 kayıtları silinmez.
- Karar türleri: **U** = kullanıcı kararı, **T** = teknik lider kararı (`requirements.md`).
- İlgili: ADR-0003, ADR-0006, ADR-0012, ADR-0014, ADR-0015, ADR-0016 (DSL v1), `architecture.md` §6.3–§6.6, `m2-protocol.md` v1, `m1-data-policy.md` v1.2, `testing-strategy.md` §4, §7.
- `m2-protocol.md` v1'deki veri, ısınma/boşluk, olay sırası, koruyucu stop, maliyet modeli v1, allocation v1 ve portföy riski kuralları (§1–§2, §6–§9) aynen geçerlidir; bu belge yalnızca ekler.

## 1. İndikatör havuzu (U: kapsam; T: formüller)
Tüm fiyat hesapları `decimal` ile yapılır. Karekök gereken yerde değer IEEE `double` ile hesaplanır ve 12 anlamlı basamağa yuvarlanarak `decimal`'e çevrilir (m2-protocol §9'daki σ kuralıyla aynı). `L` = ilk tanımlı değer için gereken kesintisiz mum sayısı. DSL v1'de indikatör girdisi `close`'tur; hacim indikatörü `QuoteVolume` (USDT) kullanır.

M2 indikatörleri (m2-protocol §3) değişmez: **EMA(n)** `L = n`; **ATR(n)** `L = n`; **RSI(n)** `L = n + 1`; **Supertrend(n, m)** `L = n`, çıktılar `upper`, `lower`, `dir` (yukarı = +1, aşağı = −1).

Yeni indikatörler:
- **SMA(n):** son n girdinin aritmetik ortalaması. `L = n`.
- **MACD(f, s, g)**, `f < s`: `line = EMA_f − EMA_s` (EMA_s'in ilk değerinden itibaren). `signal` = `line` serisinin EMA_g'si; ilk değeri, ilk g `line` değerinin basit ortalaması. `hist = line − signal`. `L = s + g − 1`.
- **ADX(n), Wilder:** ikinci mumdan itibaren `up = H_t − H_{t−1}`, `down = L_{t−1} − L_t`; `+DM = up` (up > down ve up > 0 ise), değilse 0; `−DM = down` (down > up ve down > 0 ise), değilse 0; `TR = max(H − L, |H − C_{t−1}|, |L − C_{t−1}|)`. Düzleştirilmiş `TR_n`, `+DM_n`, `−DM_n`: ilk değer ilk n değerin **toplamı** (n + 1. mumda), sonra `S_t = S_{t−1} − S_{t−1}/n + x_t`. `+DI = 100·+DM_n / TR_n`, `−DI = 100·−DM_n / TR_n` (`TR_n = 0` ise ikisi de 0). `DX = 100·|+DI − −DI| / (+DI + −DI)` (payda 0 ise 0). `adx`'in ilk değeri ilk n DX'in basit ortalaması, sonra `ADX_t = (ADX_{t−1}·(n − 1) + DX_t) / n`. Çıktılar `plus_di`, `minus_di` (`L = n + 1`) ve `adx` (`L = 2n`).
- **Bollinger(n, k):** `middle = SMA_n(C)`; `σ = sqrt(Σ(C_i − middle)² / n)` (popülasyon, son n kapanış); `upper = middle + k·σ`, `lower = middle − k·σ`; `bandwidth = (upper − lower) / middle` (`middle = 0` ise tanımsız); `percent_b = (C − lower) / (upper − lower)` (`upper = lower` ise tanımsız). `L = n`.
- **Donchian(n):** **mevcut mum hariç** önceki n mum üzerinden `upper = max(H)`, `lower = min(L)`, `middle = (upper + lower) / 2`. Böylece "kapanış > upper" bir kırılımı ifade eder ve mum kendisiyle karşılaştırılmaz. `L = n + 1`.
- **ROC(n):** `C_t / C_{t−n} − 1` (oran; yüzde değil). `L = n + 1`.
- **VolumeSMA(n):** son n mumun `QuoteVolume` ortalaması. `L = n`.

**Tanımsız değer semantiği:** ısınmadaki ya da tanımsız bir indikatör değeri `Unknown`'dur. Karşılaştırmalar `Unknown` bir operandla `Unknown` verir; `and`/`or`/`not` Kleene üç değerli mantığıyla değerlendirilir. Giriş ve kapanış sinyali yalnızca koşul `True` ise üretilir (`not` ile `Unknown` asla `True`'ya çevrilemez).

## 2. Piyasa rejim filtresi (U: kapsam; T: kurallar)
- DSL operandı ya da indikatörü `source: "btc"` ile `BTCUSDT`'nin aynı aralıktaki serisine bağlanabilir. Değerlendirilen parite `BTCUSDT` ise `btc` kaynağı paritenin kendi serisidir.
- **Hizalama:** paritenin `t` mumu kapanışında yalnızca **aynı `open_time`'lı** BTC mumu ve öncekiler kullanılır. Her iki mum aynı anda kapanır; look-ahead yoktur.
- BTC'nin o `open_time`'lı mumu eksikse ya da BTC indikatörü ısınmadaysa, BTC'ye bağlı tüm atomlar `Unknown`'dur (§1): giriş açılmaz, kapanış sinyali bu atom yüzünden tetiklenmez; koruyucu stop her zaman çalışır.
- BTC boşluğu BTC'ye bağlı indikatörlerin durumunu sıfırlar (m2-protocol §2 kuralı BTC serisine uygulanır); paritenin kendi durumunu sıfırlamaz. Paritenin ısınma uzunluğu `L` yalnızca kendi indikatörlerinden hesaplanır.
- Bir test, BTC serisinin `t`'den sonraki mumlarının değiştirilmesinin `t`'ye kadarki sinyalleri değiştirmediğini doğrular (look-ahead property).

## 3. DSL v1 (T; ayrıntılı şema ADR-0016)
- Bir strateji: aralık (v1'de yalnızca `4h`), en fazla 8 adlandırılmış indikatör, bir giriş koşulu, bir kapanış koşulu ve isteğe bağlı bir strateji stop'u.
- **Operatörler:** `gt`, `gte`, `lt`, `lte`, `cross_above`, `cross_below`, `and`, `or`, `not`. `cross_above(a, b)` ⇔ `a_t > b_t` ve `a_{t−1} ≤ b_{t−1}`; `cross_below(a, b)` ⇔ `a_t < b_t` ve `a_{t−1} ≥ b_{t−1}` (m2-protocol §4 ile aynı).
- **Operandlar:** indikatör çıktısı (`lag` ∈ {0, 1}), fiyat alanı (`open`, `high`, `low`, `close`, `volume`; `source` self ya da btc), sabit. İndikatör ve fiyat operandları isteğe bağlı pozitif bir çarpan (`mult`) alabilir (ör. `volume > 1.5 × vol_sma`).
- **Strateji stop'u** (ADR-0014): `percent` (giriş dolumu × (1 − p)), `atr` (giriş dolumu − m·ATR(n), karar mumundaki ATR, sabit) ya da `indicator` (karar mumundaki bir indikatör seviyesi; `trailing: true` ise her kapanışta yalnızca yukarı çekilir ve bir sonraki mumdan geçerli olur). Efektif stop, D25 hard stop'u ile birlikte m2-protocol §6'daki gibi hesaplanır.
- **Isınma:** stratejinin `L`'i = self kaynaklı indikatörlerin en büyük `L`'i + 1 (lag/kesişim için önceki değer). Bu, M2 baseline'larındaki `L = 101` ve `L = 11` ile aynıdır.
- **Baseline parity:** `ema-trend` ve `supertrend`'in DSL karşılıkları (ADR-0016 ekindeki tanımlar), aynı girdilerle C# baseline'larıyla birebir aynı işlem listesini ve equity serisini üretmelidir. Bu iki DSL tanımı **yeni deneme değildir;** M2'nin kayıtlı iki denemesinin yeniden ifadesidir (§8).

## 4. Birincil metrik, benchmark ve test (U: α, güç, δ, δ biçimi, DD, benchmark; T: formüller)
- **Birincil metrik `M`:** maliyetler düşülmüş, yıllıklandırılmış log getiri: `M = mean(r) × 2190`; `r_t = ln(E_t / E_{t−1})`, `E` her 4h kapanışındaki equity. **Benchmark nakittir** (U, getiri 0); excess getiri `M`'nin kendisidir.
- **İkincil (yalnızca betimleyici):** BTC buy-and-hold ve eşit ağırlıklı sepete göre fark, toplam getiri, CAGR, maks. DD, Sharpe, işlem sayısı, kazanma oranı, maruziyet, devir (m2-protocol §11).
- **H0:** `M ≤ 0`; tek yönlü test.
- **α:** kohort içi aile düzeyi `α_fam = 0,05` (U). Ailenin ikinci ileriye dönük denemesinde Bonferroni ile `α_fam = 0,025`. Ardından kohort içinde Holm: p-değerleri artan sıralanır; `k`. sıradaki strateji için `α_k = α_fam / (m − k + 1)` (m = kohorttaki strateji sayısı). Önceki sıradaki bir strateji reddedilemediyse sonrakiler `Validated` olamaz.
- **Bootstrap:** 4h log getirileri üzerinde stationary block bootstrap (Politis–Romano); ortalama blok uzunluğu `b = ⌈n^{1/3}⌉`, `B = 10.000` tekrar, seed manifestoda. Her tekrarda `M*` hesaplanır. `p = #{M* ≤ 0} / B`. Tek yönlü sınırlar: alt sınır = `M*`'ların `⌈α_k·B⌉`. küçüğü, üst sınır = `⌈(1 − α_k)·B⌉`. küçüğü. "Alt sınır > 0" karar koşuludur (§6.5'teki "düzeltilmiş p < α" ile eşdeğer kabul edilir).
- **Hedef güç:** 0,80 (U).
- **Ekonomik minimum etki (U):** `δ = min(max(0,05; 0,50 × M_R), 0,15)`. `M_R`, aynı stratejinin aynı parite setindeki araştırma penceresi `R` üzerindeki birincil metriğidir (`policy` koşusu). `M_R ≤ 0,10` ise `δ = 0,05`. `δ` manifestoya yazılır.
- **δ'nın uygulanma biçimi (U): (a)** — `Validated` için alt sınır > 0 **ve** nokta tahmini `M ≥ δ`.
- **Maksimum drawdown kuralı (U):** ileriye dönük pencerede, 4h kapanışlarındaki equity serisinin maksimum drawdown'ı **> 0,25** ise sonuç `Failed` (işlem sayısı ve güçten bağımsız).
- **Değerlendirilen koşu:** `policy` (drawdown halt açık, m2-protocol §9). Pencere başında sermaye 10.000 USDT; indikatör ısınması pencere öncesi veriden yapılır, ısınma mumlarında işlem yoktur.
- **Karar sırası:** `architecture.md` §6.5 tablosu aynen (1 `Invalid` → 2 protokol eksik → 3 DD > 0,25 `Failed` → 4 üst sınır < δ `Failed` → 5 gücü yetersiz ya da işlem < plan `Inconclusive` → 6 `Validated` → 7 `Inconclusive`).

## 5. Süre ve güç aracı (T)
Girdiler yalnızca `R`'den gelir.
- **Planlama koşusu:** stratejinin `no-halt` koşusu (halt'ın sansürlemediği sinyal sıklığı ve getiri dağılımı için). Günlük log getiriler, gün sonu equity'sinden (20:00 açılışlı 4h mumun kapanışı).
- **Rejim etiketleri:** `R`'deki her gün için BTC'nin 90 günlük log getirisinin işareti (yukarı/aşağı) × BTC'nin 30 günlük gerçekleşen volatilitesinin `R` üzerindeki tertili → 6 rejim. 60 günden az gözlemi olan rejim kullanılmaz.
- **Rejim başına:** uzun dönem varyansı `σ²_LR` (günlük getirilerin ortalamasının stationary bootstrap varyansı × n; `b = ⌈n^{1/3}⌉`, `B = 10.000`) ve işlem sıklığı `f` (işlem/gün).
- **En kötü rejim:** en büyük `σ_LR` ve ayrıca en küçük `f` (ikisi farklı rejimlerden gelebilir; muhafazakâr).
- **Güç:** gerçek yıllık etki `δ` iken `n` günlük pencerede "alt sınır > 0" koşulunun sağlanma olasılığı, normal yaklaşımla: `güç(n) = Φ( (δ/365)·√n / σ_LR − z_{1−α'} )`; planlamada `α' = α_fam / m` (muhafazakâr Bonferroni).
  - **Not:** güç tanımı δ koşulunu (a) **içermez.** İçerseydi, gerçek etki tam δ iken nokta tahmininin δ'yı geçme olasılığı yaklaşık %50 olacağı için hiçbir süre %80 güce ulaşamazdı. δ koşulu karar anında ayrıca uygulanır.
- **Süre:** `n_days` = gücün ≥ 0,80 olduğu en küçük ay sayısı (6…24 ay, takvim ayı, `A_c`'den itibaren). `L_req = max(n_days, n_trades / f)` (aşağıdaki `n_trades` ile). `L_c = max(L_req, 6 ay)`, **yukarı** tam aya yuvarlanır. `L_req > 12 ay` ya da 24 ayda bile güce ulaşılamıyorsa kohort **gücü yetersiz** işaretlenir ve `L_c = 12 ay` (L_max) olur.
- **Planlanan asgari işlem sayısı:** `n_trades = max(10, ⌈0,5 × f_worst × L_c⌉)`. İleriye dönük pencerede gerçekleşen işlem sayısı bunun altındaysa sonuç en iyi ihtimalle `Inconclusive`.
- Araç çıktıları (rejim tablosu, `σ_LR`, `f`, güç eğrisi, `L_c`, `n_trades`, underpowered bayrağı) manifestonun güç artefaktlarıdır.

## 6. Araştırma kapıları (T; eşikler §7'deki kalibrasyonla)
Tüm kapılar `R` üzerinde, `policy` koşusuyla, maliyet modeli v1 ile.
| # | Kapı | Kural |
|---|---|---|
| G1 | Maliyet sonrası getiri | `M_R > 0` (nakit benchmark) |
| G2 | İşlem sayısı ve güven | işlem ≥ `N_min`; §4 bootstrap'ıyla tek yönlü %95 alt sınır > 0 |
| G3 | Walk-forward tutarlılığı | `R` takvim yıllarına bölünür (2022, 2023, 2024, 2025); `M` > 0 olan yılların oranı ≥ `τ_wf`. Parametre ayarlayan akışlarda (M4 grid) ankore genişleyen pencere: parametre `[U, yıl_k)`'da seçilir, `yıl_k`'da test edilir; aynı oran kuralı |
| G4 | Parametre komşuluğu | her sayısal parametre tek tek ±%10 (tam sayılarda en az ±1, sınırlar içinde); komşuların `M_R > 0` oranı ≥ `τ_nb` |
| G5 | Çoklu varlık | işlem gören paritelerin ≥ %30'unda parite PnL'i > 0 ve tek bir paritenin payı toplam pozitif PnL'in ≤ %50'si (sabit, kalibre edilmez) |
| G6 | Deflated Sharpe | DSR olasılığı (Bailey ve López de Prado; çarpıklık ve basıklık düzeltmeli) ≥ `τ_dsr`. Deneme sayısı `N` = kesim dönemindeki **global** deneme sayacı (bu deneme dahil; aile sayısı değil, muhafazakâr). Deneme Sharpe'larının varyansı kayıtlı denemelerden; 10'dan az deneme varsa kalibrasyondaki null dağılımın varyansı |
Benchmark karşılaştırması (BTC, sepet) raporlanır ama kapı değildir (birincil benchmark nakit, U).

## 7. Kalibrasyon protokolü (T)
- **Hedef yanlış pozitif oranı:** tüm kapılardan birlikte geçme olasılığı null altında **≤ %5.**
- **Null veri:** 20 sentetik parite, 4h, `R` uzunluğunda (8.766 mum) + ısınma; sıfır sürüklenmeli log random walk; parite başına 4h log volatilite `[0,008; 0,030]` aralığından tekdüze çekilir; OHLC ve hacim, seed'li deterministik üreteçle. 20 farklı evren seed'i.
- **Null stratejiler:** ADR-0016 beyaz listesinden seed'li rastgele üreteçle 1.000 geçerli DSL stratejisi (validator'dan geçen); her biri bir evrende.
- **Doğru pozitif:** 50 seed'li evrende kalıcı trend rejimleri gömülü sentetik veri (4h getirilerinde parite başına pozitif otokorelasyonlu sürüklenme blokları; yıllık beklenen etki bu stratejide maliyet sonrası ≈ %15); bu yapıyı kullanan sabit bir EMA kesişim stratejisi. Hedef: geçme oranı ≥ **%80.**
- **Aday eşik ızgarası:** `N_min` ∈ {20, 30, 50, 100}; `τ_wf` ∈ {0,50; 0,75; 1,00}; `τ_nb` ∈ {0,50; 0,60; 0,70; 0,80}; `τ_dsr` ∈ {0,90; 0,95; 0,975}.
- **Seçim kuralı:** FP ≤ %5 ve TP ≥ %80 olan bileşimler içinde TP'si en yüksek olan; eşitlikte daha sıkı olan (sözlük sırasıyla `τ_dsr`, `τ_wf`, `τ_nb`, `N_min` büyük olan). Hiçbir bileşim iki şartı sağlamıyorsa eşikler **sessizce gevşetilmez;** rapor hazırlanır ve durulur.
- Kalibrasyon koşuları sentetik veridir ve deneme sayacına girmez. Sonuçlar `docs/m3-calibration-results.md`'ye (yalnızca metrikler ve seçilen eşikler) yazılır.

## 8. Aile kimliği ve deneme muhasebesi (T; ADR-0006, ADR-0015)
- **Normalizasyon:** kanonik JSON (alan sırası ve boşluk bağımsız); `and`/`or` argümanları kanonik sıraya dizilir ve tekrarlar kaldırılır; tek argümanlı `and`/`or` açılır; `not(not(x))` = `x`; `lt(a, b)` → `gt(b, a)`, `lte(a, b)` → `gte(b, a)`; indikatör kimlikleri (`id`) yapıdaki konumlarına göre yeniden adlandırılır.
- **Yapı hash'i:** normalize tanımdan parametre değerleri (periyotlar, çarpanlar, sabitler, `mult`, stop parametreleri), aralık, parite seti, model sürümleri, benchmark ve engine çıkarıldıktan sonra kalan iskeletin SHA-256'sı. Tanım hash'i: tam normalize tanımın SHA-256'sı.
- **Benzerlik (T21):** yapı hash'i yeni ise aday yalnızca aile temsilcileriyle karşılaştırılır; `R` üzerinde pozisyon korelasyonu ≥ **0,9** ya da giriş Jaccard'ı ≥ **0,8** ise mevcut aileye atanır (mühendislik başlangıç değerleri).
- **Deneme sayacı:** `R` üzerinde kaydedilen her yeni tanım hash'i (köken: `llm`, `grid`, `human`, `dev-assisted`) bir denemedir; aynı tanım hash'inin tekrar koşusu yeni deneme değildir. Sayaç kesim dönemi boyunca globaldir. M2'nin iki baseline'ı geriye dönük olarak 2 deneme (`dev-assisted`) kaydedilir; DSL karşılıkları aynı denemelere bağlanır.

## 9. Manifesto, sınırlar ve evaluator (T; architecture §6.2–§6.5)
- **Manifesto alanları:** architecture §6.3 listesi + bu protokolün sürümü ve commit'i, `δ`, `M_R`, `α_fam`, deneme sırası (1. ya da 2.), bootstrap seed'i, güç artefaktları, `L_c`, `n_trades`, underpowered bayrağı. Bloke edici alanlardan (α, güç, δ, δ biçimi, DD kuralı) biri eksikse kohort "doğrulamaya uygun değil" olarak işaretlenir.
- **Sınırlar (U, D21):** en fazla 2 açık kohort; kohort başına en fazla 3 strateji; aile başına en fazla 2 ileriye dönük deneme; tanısal holdout aile başına ömür boyu 1, kesim dönemi başına 10.
- **Açığa çıkan özet:** birincil metrik ve maks. DD yüzde olarak 1 ondalığa yuvarlanır; işlem sayısı tam sayı; sonuç durumu.
- **Yayın tamponu:** 3 gün (M5'te gözden geçirilir).
- **M3 kapsamı:** evaluator yalnızca seed'li **sentetik** pencere verisiyle çalışır; gerçek holdout verisi indirilmez (roadmap M3 notu).

## 10. Kohort 0 (U: kapsam ve sıra; T: ayrıntılar)
- **Stratejiler:** `ema-trend` v1 ve `supertrend` v1'in DSL karşılıkları. Parite seti `core-4y-S` (20 parite), 4h, maliyet modeli v1, allocation v1, `RiskConfig` (D25 = %10, m2-protocol §9), başlangıç sermayesi 10.000 USDT.
- **Amaç:** boru hattının ve manifestonun uçtan uca doğrulanması (`pipeline-validation`). Araştırma kapılarından (§6) **muaftır;** bu muafiyet manifestoda yazılıdır ve sonucu değiştirmez.
- `M_R` her iki stratejide negatif olduğundan `δ = 0,05`.
- **Sıra:** yalnızca bu protokol onaylandıktan sonra dondurulur. `registered_at` = Kohort 0 manifestosunun eklendiği yerel commit'in zamanı; `A_0` buradan türetilir (architecture §6.3 istisnası). M5'te özgün değerler korunarak aktarılır.
- Süre aracı Kohort 0 için büyük olasılıkla "gücü yetersiz" verecektir; bu kaydedilir, değiştirilmez.

## 11. E1 kategorisi (U: alt küme; T: kurallar)
- Kategori tanımı ADR-0012: `U0 = first_open + 30 gün`; E1 ⇔ çekirdek değil, `U0 ≤ 2025-01-01T00:00Z` ve kalite kuralları `[U0, C)` üzerinde sağlanıyor.
- **Kalite:** `m1-data-policy.md` §6 aynen (≥ %99 mum, en uzun boşluk ≤ 72 × 1h / 18 × 4h, checksum zorunlu, bütünlük hataları). ADR-0012'deki "kayıtlı kesinti" istisnası **uygulanmaz** (M1 politikasıyla tutarlılık). Payda `[U0, C)` aralığındaki beklenen mum sayısıdır.
- `< U0` (karantina) verisi yalnızca indikatör ısınması için kullanılır, sinyal üretilmez.
- Dışlama listesi (`m1-exclusions.v1`) E1'e de uygulanır.
- **M3 alt kümesi (U):** E1 adayları M1 sıralama kuralıyla (quote hacim medyanı) sıralanır, ilk 5'i 1h ve 4h olarak kullanıcının etkileşimli Kapı-2 koşusuyla import edilir. Bu alt küme M3'te yalnızca boru hattı doğrulaması içindir; E1 araştırması için evren değildir.

## 12. Tekrar üretilebilirlik
Her koşu ve kalibrasyon: veri snapshot hash'i (ya da sentetik seed listesi), strateji tanım hash'i, protokol sürümü ve commit'i, engine git SHA'sı, maliyet/allocation/risk sürümleri, bootstrap seed'i ve sonuç hash'i ile kaydedilir. Aynı girdiler aynı sonuç hash'ini üretmelidir. M2'nin dört koşusunun sonuç hash'leri M3 engine değişikliklerinden sonra değişmemelidir (regresyon kilidi).
