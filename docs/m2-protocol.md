# M2 Protokolü — Baseline backtest ön kaydı (v1)

- Durum: **Ön kayıt.** Bu belge, hiçbir backtest çalıştırılmadan ve hiçbir parametre denenmeden yazıldı. Ön kayıt kanıtı, belgenin eklendiği git commit'idir (commit SHA). Kural değişikliği yeni sürüm (v2) ve yeni deneme demektir; v1 sonuçları silinmez.
- Karar türleri: **U** = kullanıcı kararı, **T** = teknik lider kararı (`requirements.md`).
- İlgili: ADR-0011 (allocation), ADR-0014 (çıkışlar ve stop), ADR-0015 (parity), `m1-data-policy.md` v1.2, `m1-core-universe.md`, `risk-model.md`.

## 1. Veri (U)
- Evren: `m1-core-universe.md`'deki 20 çekirdek parite (`core-4y-S`; survivor-conditioned). Snapshot `a6c4e89b98364a2e2b3479e81a9e7188ffb27e08a4d444723523e413f7509e82`.
- Kaynak: `market.candles`, aralık **4h** (U). İşlem dönemi `R = [2022-01-01T00:00Z, 2026-01-01T00:00Z)`. Isınma: `[2021-07-05T00:00Z, 2022-01-01T00:00Z)` (politika §2).
- Sinyaller yalnızca açılışı `≥ U` olan mumların kapanışında değerlendirilir; daha önceki mumlar yalnızca indikatör ısınması içindir.

## 2. Isınma ve boşluk kuralı (U)
- Bir parite için beklenen bir 4h mum eksikse, o paritenin tüm indikatör durumu sıfırlanır.
- Strateji, son sıfırlamadan sonra `L` kesintisiz geçerli mum birikene kadar o paritede yeni giriş ya da kapanış sinyali üretmez. `L`, stratejinin en uzun lookback'idir (§4, §5).
- Bu sürede açık pozisyon tutulur; koruyucu stop (§6) çalışmaya devam eder ama seviyesi güncellenmez.
- Eksik mumda o parite için dolum ve stop kontrolü yapılmaz; bekleyen emir iptal edilir.

## 3. İndikatör tanımları (T)
Tüm fiyat hesapları `decimal` ile yapılır.
- **EMA(n):** α = 2 / (n + 1). İlk değer, ilk n kapanışın basit ortalaması (n. mumda). Sonra `EMA_t = α·C_t + (1 − α)·EMA_{t−1}`.
- **TR:** ilk mumda `H − L`; sonra `max(H − L, |H − C_{t−1}|, |L − C_{t−1}|)`.
- **ATR(n), Wilder:** ilk değer, ilk n TR'nin basit ortalaması (n. mumda). Sonra `ATR_t = (ATR_{t−1}·(n − 1) + TR_t) / n`.
- **RSI(n), Wilder:** değişim `Δ_t = C_t − C_{t−1}`. İlk ortalama kazanç/kayıp, ilk n değişimin basit ortalaması (n + 1. mumda). Sonra `avg_t = (avg_{t−1}·(n − 1) + x_t) / n`. `RSI = 100 − 100 / (1 + kazanç/kayıp)`; ortalama kayıp 0 ise 100. (Baseline'larda kullanılmaz; M2 kapsamındaki indikatör.)
- **Supertrend(n, m):** `hl2 = (H + L)/2`; temel bantlar `hl2 ± m·ATR(n)`. İlk ATR değerinin olduğu mumda son bantlar temel bantlara eşittir ve yön, kapanış temel üst bandın üstündeyse yukarı, değilse aşağıdır. Sonra:
  - son üst bant = temel üst < önceki son üst **veya** önceki kapanış > önceki son üst ise temel üst, değilse önceki son üst;
  - son alt bant = temel alt > önceki son alt **veya** önceki kapanış < önceki son alt ise temel alt, değilse önceki son alt;
  - yön: önceki yön aşağı ve kapanış > son üst ise yukarı; önceki yön yukarı ve kapanış < son alt ise aşağı; aksi halde değişmez.

## 4. Baseline A — EMA 50/100 trend v1 (T)
- **Giriş:** kapanışta `EMA50 > EMA100` ve önceki kapanışta `EMA50 ≤ EMA100` (yukarı kesişim).
- **Kapanış çıkışı:** kapanışta `EMA50 < EMA100` ve önceki kapanışta `EMA50 ≥ EMA100`.
- **Strateji stop'u:** `giriş dolumu − 3·ATR(14)` (karar mumundaki ATR); sabit.
- `L` = 101 (EMA100 değeri ve kesişim için bir önceki değer).

## 5. Baseline B — Supertrend(10, 3) v1 (T)
- **Giriş:** kapanışta yön aşağıdan yukarıya döner.
- **Kapanış çıkışı:** kapanışta yön yukarıdan aşağıya döner.
- **Strateji stop'u:** girişte karar mumunun son alt bandı; her kapanışta son alt bant yükselirse stop o seviyeye çekilir (yalnızca yukarı), yeni seviye bir sonraki mumdan geçerlidir (ADR-0014).
- `L` = 11 (ATR10 değeri ve yön dönüşü için bir önceki değer).

## 6. Koruyucu stop (U: D25; T: ADR-0014)
- **Efektif stop:** `max(strateji stop'u, giriş dolumu × 0,90)` — girişe en yakın olan (D25 = %10).
- **OHLC kuralı:** pozisyon açıkken her mumda önce stop: `Open ≤ S` ise çıkış `Open`'dan; değilse `Low ≤ S` ise çıkış `S`'den. Giriş mumunda yalnızca `Low ≤ S` kuralı uygulanır (giriş `Open`'da yapıldı). Aynı mumda stop, kapanış sinyalinden önce gelir.

## 7. Olay sırası (T)
Her 4h mum `t` için:
1. **Açılış:** `t−1` kapanışında verilen emirler `Open_t`'de dolar: önce çıkışlar, sonra girişler.
2. **Mum içi:** açık pozisyonlarda koruyucu stop kontrolü (§6).
3. **Kapanış:** equity piyasa değeriyle ölçülür; portföy risk kuralları (§9) değerlendirilir; indikatörler güncellenir; sinyaller üretilir; stop seviyeleri güncellenir; emirler `t+1` açılışı için sıraya girer.
- Son mumda (açılışı 2025-12-31T20:00Z) açık pozisyonlar kapanış fiyatından, açılış dolumu maliyetleriyle kapatılır.

## 8. Maliyet modeli v1 (T)
- Ücret: işlem tutarının **10 bps**'i, her tarafta.
- Slippage: açılış ve son kapanış dolumlarında olumsuz yönde **5 bps**; stop dolumlarında olumsuz yönde **20 bps**.
- Alımda fiyat `× (1 + slip)`, satımda `× (1 − slip)`; ücret dolum tutarından. Maliyetler negatif olamaz.
- 10 USDT'nin altındaki emirler gönderilmez (`MIN_NOTIONAL` yaklaşımı).

## 9. Allocation v1 ve portföy riski (T: ADR-0011; U: D7, D25)
- **Volatilite:** UTC gün sonu kapanışı = o günün 20:00 açılışlı 4h mumunun kapanışı. σ = karar anına kadar kapanmış son 31 gün sonu kapanışından hesaplanan 30 günlük log getirilerinin örneklem standart sapması. Pencerede bir gün sonu eksikse ya da σ = 0 ise σ tanımsızdır → ağırlık 0. (σ IEEE `double` ile hesaplanır ve 12 anlamlı basamağa yuvarlanır.)
- **Ağırlık:** giriş anında `w_i = 0,80 × (1/σ_i) / Σ_j (1/σ_j)`; toplam, o anda açık pozisyonlar ile yeni girecek adaylar üzerindendir. Her `w_i` %20'de kırpılır; artan pay nakitte kalır.
- **Boyut:** `w_i × equity` (karar kapanışındaki equity) / dolum fiyatı. Girişte sabitlenir; çıkışa kadar yeniden dengeleme yok. Toplam maruziyet %80'i ya da nakit yetmezse yeni emirler orantılı küçültülür.
- **Seçim:** en fazla 5 açık pozisyon. Boş yuvadan fazla aday varsa mevcut pozisyonlar korunur, yeni girişler M1 çekirdek sırasına göre (küçük sıra önce) seçilir.
- **Günlük kayıp:** bir 4h kapanışında equity, aynı UTC gününün başındaki equity'ye göre %3 ya da daha fazla düşükse, 24 saat boyunca yeni giriş açılmaz.
- **Drawdown halt (U):** equity, o ana kadarki zirvesinin %85'ine ya da altına düşerse halt: koşunun sonuna kadar yeni giriş yok; açık pozisyonlar stop ya da kapanış çıkışıyla kapanır. Her baseline iki koşu üretir:
  - **birincil** (`policy`): halt açık;
  - **tanısal** (`no-halt`): halt kapalı, diğer kurallar aynı.

  Rapor birincil koşuyu esas alır.

## 10. Benchmark'lar (U, T)
Başlangıç sermayesi 10.000 USDT; maliyet modeli §8. Benchmark'lar portföy risk kurallarına tabi değildir.
- **BTC buy-and-hold:** U'daki ilk 4h açılışta tüm sermayeyle BTCUSDT alımı; son mumun kapanışında satış.
- **Eşit ağırlıklı sepet:** U'daki ilk açılışta 20 paritenin her birine %5; her ayın ilk 4h açılışında eşit ağırlığa yeniden dengeleme (yalnızca alım-satım farkı için maliyet); son kapanışta satış. Bir paritenin o mumu eksikse dengelemesi bir sonraki mevcut açılışa kayar.
- **Nakit:** getiri 0.

## 11. Metrikler (T)
Equity serisi her 4h kapanışında, `R` boyunca: toplam getiri, CAGR (1461 gün), maksimum drawdown, Sharpe (4h getirileri, yılda 2.190 dönem, risksiz oran 0), işlem sayısı, kazanma oranı, ortalama maruziyet, devir, ödenen ücret ve slippage. Kapı eşikleri M3'e aittir; M2 yalnızca rapor üretir.

## 12. Deneme muhasebesi ve tekrar üretilebilirlik (T: T18)
- Her koşu `research.experiments`'e eklenir: strateji adı ve sürümü, strateji tanımı hash'i, koşu türü (`policy` / `no-halt`), protokol sürümü ve commit'i, veri snapshot hash'i, engine git SHA'sı, sonuç hash'i (işlemler ve equity serisi üzerinden SHA-256), metrikler.
- Bu protokol iki strateji ve iki koşu türü tanımlar: **2 deneme**, her biri iki raporlanmış koşu.
- Aynı girdilerle iki koşu aynı sonuç hash'ini üretmelidir.
