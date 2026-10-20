# ADR-0014: Kapanış çıkış sinyali ve mum içi koruyucu stop

- Durum: Kabul edildi · Tarih: 2026-10-14
- Karar türü: Teknik karar T20; kullanıcı kararları D11 (rev.) ve D25
- Uygulama durumu: **Backtest'te M2'den, paper'da M5'ten beri uygulanıyor (2026-10-20).** Paper ve backtest aynı motor kodunu kullanıyor (ADR-0018). Testnet'teki borsa stop emri M6'da.
- İlgili: ADR-0003, ADR-0011, ADR-0015, `architecture.md` §5, `risk-model.md` §2–§4

## Bağlam
- Karar döngüsü saatliktir ve 1h/4h kapanan mumlarla çalışır (D2).
- Kripto piyasasında bir saat içinde %10–15'lik hareketler olabilir. Yalnızca mum kapanışında çalışan bir çıkış, zararı geç keser.
- Backtest (M2), paper (M5) ve Testnet (M6) aynı kernel kodunu kullanmalıdır (`requirements.md` §6, parity). Mum içi davranış her ortamda farklı modellenirse parity bozulur.

## Karar

### 1. İki çıkış türü
| Tür | Ne zaman üretilir | Ne zaman uygulanır |
|---|---|---|
| **Kapanış çıkış sinyali** (strateji kuralı) | `t` mumu kapandığında | `t+1` mumunun açılışında |
| **Koruyucu stop** | Girişte seviye `S` belirlenir | Mum içinde, fiyat `S`'ye değdiği anda |

- Girişler de kapanış sinyalidir: `t` kapanışında üretilir, `t+1` açılışında uygulanır.
- **Stop seviyesi:** girişte, giriş anında bilinen veriden deterministik olarak hesaplanır. İki kaynağı vardır:
  - strateji stop'u: strateji tanımının sürümlü bir parçasıdır (örneğin ATR katı);
  - risk hard stop'u: `RiskConfig`'teki "pozisyon başına maksimum kayıp" (D25; değer %10, 2026-10-14).
  - **Efektif stop**, ikisinden giriş fiyatına en yakın olanıdır.
- Stop seviyesi yalnızca mum kapanışında güncellenebilir (örneğin iz süren stop); yeni seviye bir sonraki mumdan itibaren geçerlidir. Mum içinde seviye değişmez.

### 2. Tek simülasyon kuralı (backtest ve paper)
Pozisyon açıkken her mum için, sırayla:
1. `Open ≤ S` ise çıkış **`Open`** fiyatından (gap).
2. Değilse `Low ≤ S` ise çıkış **`S`** fiyatından.
3. Stop tetiklenmediyse, mum kapanışında strateji kuralları değerlendirilir; bir çıkış sinyali `t+1` açılışında uygulanır.

- Aynı mumda hem stop hem kapanış sinyali varsa **stop önceliklidir** (mum içinde daha önce gerçekleşmiştir).
- Giriş mumu da kurala tabidir: giriş `Open`'da olur, aynı mumun `Low`'u `S`'ye değerse çıkış aynı mumda gerçekleşir.
- Dolum fiyatına sürümlü maliyet modelinden ücret ve slippage uygulanır. Stop dolumları için ayrı bir slippage parametresi vardır (`requirements.md` §8).
- Kural yalnızca OHLC'ye bakar; mum içindeki fiyat yolu bilinmediği için başka bir varsayım yapılmaz.

### 3. Ortamlara göre uygulama
| Ortam | Uygulama | Parity |
|---|---|---|
| Backtest (M2) | §2 kuralı arşiv mumlarına uygulanır | — |
| Paper (M5) | Trading.Host tick izlemez. Her kapanışta §2 kuralını kapanan muma uygular ve dolumu kuralın verdiği fiyattan kaydeder | Aynı mumlarla backtest ile **birebir** (ADR-0015, L1/L2) |
| Testnet (M6) | Giriş dolumundan sonra borsaya `S` seviyesinde koruyucu bir stop emri bırakılır; borsa mum içinde tetikler | Birebir değildir. Sapmalar (gap, slippage, stop-limit emrinin dolmaması, tick yuvarlaması, borsa reddi) fark raporunda kategorize edilir |

- Binance Spot'ta uygun stop emir tipi ve Testnet desteği **doğrulanmadı**; M6 öncesinde `sources.md`'ye işlenir.
- Testnet'teki stop emirleri transactional outbox (ADR-0002) ve reconciliation kapsamındadır; stop seviyesi kapanışta güncellenirse emir iptal edilip yeni seviyeyle yeniden gönderilir.

### 4. Kill switch ve diğer limitler
- **Kill switch (D11 rev.):** yeni girişleri durdurur ve açık giriş emirlerini iptal eder; **koruyucu stop emirlerini iptal etmez.** Çıkışlar her zaman çalışır.
- Bayat veride yalnızca çıkışlar çalışır; paper'da stop kuralı veri geldiğinde kapanan mumlara uygulanır, Testnet'te emir borsada bekler.
- Günlük kayıp (%3) ve maksimum drawdown (%15) limitleri **mum kapanışında** değerlendirilir (değişmedi).
- LLM stop'ları engelleyemez ve seviyelerini değiştiremez (D1).

## Sonuçlar
- Backtest ve paper arasında stop davranışı birebir aynıdır; Testnet sapmaları ölçülür, gizlenmez.
- OHLC kuralı, mum içinde stop'tan önce fiyatın yükselip sonra düşmesi gibi yolları ayırt edemez; gap kuralı ve stop önceliği bu belirsizliği muhafazakâr (kötümser) yönde çözer.
- Reddedilen alternatifler:
  - Paper'da tick veya 1 dakikalık veriyle mum içi izleme: backtest ile parity bozulur ve ek veri/bellek gerektirir.
  - Backtest'te stop'u kapanışta uygulamak: canlıdaki gerçek riski olduğundan düşük gösterir.
