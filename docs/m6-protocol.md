# M6 Protokolü — Binance Spot Testnet execution (v1)

- Durum: **Ön kayıt — kullanıcı tarafından onaylandı (Kapı A, 2026-10-26, U; §0.2 A-1…A-6 dahil).** Ön kayıt kanıtı, bu belgenin onaylandığı git commit'idir. O commit'ten önce M6 kodu yazılmaz.
- Karar türleri: **U** = kullanıcı, **T** = teknik lider, **P** = öneri (kullanıcı onayı gerekir).
- İlgili: ADR-0002, ADR-0012, ADR-0013, ADR-0014, ADR-0015, ADR-0018, ADR-0019, ADR-0020, **ADR-0021**; `architecture.md` §5, §10; `risk-model.md` §2–§4; `testing-strategy.md` §5, V10; `sources.md` S-B1…S-B5, S-B11…S-B24; D3, D6, D7, D11, D25, D29, D30, **D33**.
- Değiştirilmeyenler:
  - `RiskConfig` limitleri (D7, D22, D25, D29);
  - maliyet modeli v1;
  - M2'nin dört sonuç hash'i;
  - Kohort 0 ve Kohort 1 manifestoları ile parity L1/L2 hash'leri;
  - paper run'ları;
  - evaluator ve adında `Holdout` geçen dosyalar (M6'da değişiklik listesi **boştur**).

## 0. Kullanıcı kararları

### 0.1 Alınmış kararlar (U, 2026-10-26)
| # | Karar |
|---|---|
| U-M6-1 | M6 başlatıldı. Kapılar: **A** = belge doğrulaması (`sources.md`), bu protokol ve ADR-0021; **B** = çevrimdışı kod ve testler, `verify.ps1` yeşil (sahte HTTP handler ve fault injection); **C** = kullanıcının gerçek Testnet koşuları (≥ 20 emirlik tatbikat, `PaperTrading → TestnetActive` geçişi). D30 dolmadan gerçek strateji terfisi yapılmaz |
| U-M6-2 (**D33**) | B6 kapanır. Testnet API anahtarında statik IP kısıtı aranmaz (ev bağlantısının IP'si değişebilir; Testnet yalnızca sanal bakiye kullanır). Telafi edici kontroller: istekler kodda yalnızca `https://testnet.binance.vision` host'una kilitlidir (`TestnetGuardHandler`); 3xx reddedilir; anahtar yalnızca Trading.Host'un secret'larındadır (`/run/secrets/testnet_api_key`, `testnet_api_secret`) ve yalnızca işlem iznine sahiptir. **Karar yalnızca Testnet için geçerlidir;** gerçek borsa (mainnet) kapsam dışıdır ve ayrı karar ister |
| U-M6-3 | **D30 sayımı:** 14 gün, strateji için `PaperTrading` lifecycle olayından itibaren **takvim günüdür**. 60 mum, stratejinin kohort paper run'ında işlenmiş 4h mumlarıdır; kesinti sonrası sırayla işlenen (catch-up) mumlar dahildir. Kesinti oranı ilerleme göstergesinde ayrıca verilir |
| U-M6-4 | Bir strateji `TestnetActive` olunca **paper run'ı paralel sürer** (gölge). Testnet ayrı bir run'dır |
| U-M6-5 | **Emir tipleri:**<br>- koruyucu stop `STOP_LOSS` (tetiklenince market);<br>- sembolün `orderTypes` listesinde yoksa bantlı `STOP_LOSS_LIMIT`;<br>- giriş koruma bantlı `LIMIT` + `IOC`;<br>- sinyal çıkışı, "pozisyon kapat" ve "tümünü kapat" `MARKET` |
| U-M6-6 | İstek imzalama **HMAC-SHA256** |

### 0.2 Kapı A teknik değerleri (T; kullanıcı onayı 2026-10-26)
| # | Konu | Değer | Bölüm |
|---|---|---|---|
| A-1 | Giriş IOC koruma bandı | Referans fiyatın **+%1,0**'ı (alış limiti) | §2.2 |
| A-2 | `STOP_LOSS_LIMIT` (yedek) limit bandı | Stop fiyatının **−%2,0**'ı | §2.2 |
| A-3 | `recvWindow` ve `Unknown` çözüm ufku | 5.000 ms; `NeverAccepted` için 60 sn ek pay ve iki ardışık `-2013` | §3.3 |
| A-4 | Testnet run sermayesi | Paper ile aynı: kohort run'ı başına 10.000 USDT nominal, aynı sleeve yapısı | §4.1 |
| A-5 | Exchange bakımı sıklığı | Saatlik döngü + kill switch'te anında bir bakım geçişi | §3.1, §5 |
| A-6 | Testnet sıfırlaması | Reconciliation ile tespit; kullanıcının onaylı "yeniden taban" eylemi | §4.4 |

## 1. Kapsam ve sınırlar
- **Kapsam:**
  - borsadan bağımsız `IExchangeAdapter` ve `BinanceTestnetAdapter` (REST);
  - outbox ve emir durum makinesi; borsa filtreleri;
  - borsada koruyucu stop; D11 rev. kill switch;
  - Testnet reconciliation;
  - D30 hesaplayıcı; V10 geçişi;
  - dashboard'da Testnet ve D30 görünümü;
  - kullanıcının tatbikat harness'ı ve script'leri.
- **Kapsam dışı:**
  - WebSocket user data stream ve WebSocket API;
  - OTO/OTOCO emir listeleri;
  - Ed25519/RSA;
  - gerçek borsa (mainnet);
  - LLM;
  - 30 günlük Testnet provası (M10).
- **Claude Code:**
  - Testnet API'sine istek atmaz.
  - Testnet fiyatları, emirleri, dolumları ve bakiyeleri `trading.*` kapsamındadır ve okunmaz. Testnet fiyatları `≥ C` canlı piyasa verisi sayılır.
  - `m6-*.ps1` script'lerini ve Testnet tatbikatını yalnızca kullanıcı çalıştırır.
  - Raporlardan yalnızca §8.3'teki operasyonel alanlar görülür.
- **Testnet sonuçları performans kanıtı değildir** (risk-model §6). Doğrulama durumunu etkilemez (V2). `Validated` olmayan stratejiler `experimental` etiketi taşır.

## 2. Emirler

### 2.1 Durum makinesi (architecture §5'in uzantısı)
```
Intent → RiskApproved | RiskRejected | LocalRejected(filter:*)
RiskApproved → Submitting → Accepted(NEW) → PartiallyFilled → Filled | Canceled | Expired | ExchangeRejected
Submitting --timeout/5xx/-1007/bağlantı hatası--> Unknown --(origClientOrderId ile sorgu)--> gerçek durum | NeverAccepted (§3.3)
Accepted | PartiallyFilled --iptal isteği--> CancelRequested --> Canceled | Filled (yarış) | Unknown
```
- `EXPIRED_IN_MATCH` (STP) `Expired` olarak, `PENDING_NEW` `Accepted` olarak eşlenir (S-B15).
- Durumlar append-only olay satırlarıdır. Bir emrin güncel durumu, son olayıdır.
- Geçiş kuralları uygulamada ve DB trigger'ında ayrı ayrı uygulanır. Terminal durumlar şunlardır: `Filled`, `Canceled`, `Expired`, `ExchangeRejected`, `NeverAccepted`, `LocalRejected`.

### 2.2 Tipler ve fiyatlar (U-M6-5; A-1, A-2)
| Amaç (`purpose`) | Tip | Fiyat ve miktar |
|---|---|---|
| `entry` | `LIMIT`, `IOC` | `price = ref × 1,01`, tick'e **aşağı** yuvarlanır. `quantity = notional / price`, `stepSize`'a aşağı yuvarlanır. Dolmayan kısım borsada `EXPIRED` olur |
| `stop` | `STOP_LOSS` SELL | `stopPrice` = efektif stop `S` (D25: strateji stop'u ile hard stop'un girişe en yakını), tick'e **yukarı** yuvarlanır. Miktar, pozisyonun borsadaki miktarıdır |
| `stop` (yedek) | `STOP_LOSS_LIMIT` SELL, `GTC` | Yalnızca sembolün `orderTypes` listesinde `STOP_LOSS` yoksa. `price = S × 0,98`, tick'e aşağı yuvarlanır |
| `exit`, `close` | `MARKET` SELL | Miktar `MARKET_LOT_SIZE`/`LOT_SIZE` `stepSize`'a aşağı yuvarlanır; kalan küsurat (dust) ledger'da izlenir |

- **Referans fiyat (`ref`):** emir anında Testnet'in `GET /api/v3/avgPrice` değeri. Filtreler de referans fiyatı kullanır (S-B21). Paper'daki canlı mum (`data-api.binance.vision`) ile Testnet fiyatı arasındaki fark L3'e girer.
- Giriş zamanlaması paper ile aynıdır: karar `t` kapanışında verilir, emir kapanıştan sonraki ilk döngüde (`HH:00:15` UTC) gönderilir. Bu, `t+1` açılışına karşılık gelir.
- `newOrderRespType=FULL` (dolumlar yanıtta). Stop emirlerinde varsayılan `ACK` olduğu için `RESULT` istenir.

### 2.3 Deterministik `clientOrderId`
- Biçim: `pi-<p>-<h>`.
  - `<p>` ∈ {`e`, `s`, `x`, `c`}: entry, stop, exit, close.
  - `<h>` = SHA-256(`run|sleeve|symbol|decisionBar|purpose|attempt`) değerinin ilk 26 karakteri (küçük harf base32).
  - Uzunluk 31. Karakter kümesi `[a-zA-Z0-9-_]`. Spot FIX'in kuralı en dar olanıdır ve REST için de güvenlidir (S-B13).
- Aynı niyet, restart sonrasında da aynı kimliği üretir. `attempt` yalnızca `NeverAccepted` sonrasında artar (§3.3).
- **Benzersizlik:**
  - DB'de `client_order_id UNIQUE`;
  - `(run, sleeve, symbol, decisionBar, purpose)` için en fazla bir **etkin** (terminal olmayan) emir;
  - borsada da aynı kimlikle açık bir emir varken ikinci gönderim reddedilir (S-B4, S-B13).
- Tatbikat öneki: `drill-`. Strateji run'larının kimlikleriyle çakışmaz.

### 2.4 Filtreler (yerel ön kontrol; S-B17)
- Kontrol edilen filtreler:
  - `PRICE_FILTER`: `price` ve `stopPrice`;
  - `LOT_SIZE` ve `MARKET_LOT_SIZE`;
  - `MIN_NOTIONAL` / `NOTIONAL`: market emirde referans fiyatla, `applyToMarket` / `applyMinToMarket` / `applyMaxToMarket` bayraklarına göre;
  - `PERCENT_PRICE_BY_SIDE`;
  - `MAX_NUM_ORDERS` ve `MAX_NUM_ALGO_ORDERS`: `STOP_LOSS*` algo emir sayılır.
- Yuvarlama yönleri:
  - miktar her zaman aşağı yuvarlanır;
  - alış limiti aşağı yuvarlanır;
  - stop yukarı yuvarlanır (girişe yakın, daha korumacı);
  - stop-limit fiyatı aşağı yuvarlanır.
- **Yuvarlamadan sonra kural sağlanmıyorsa emir borsaya gitmez.** Sonuç `LocalRejected(filter:<AD>)` olur ve L2'ye `exchange-filter` nedeniyle yazılır.
- Kırpılmış notional `minNotional`'ın altında kalırsa ret yapılır (risk-model §2 "Minimum emir").
- Kurallar `exchangeInfo`'dan alınır; snapshot'ı ve hash'i kaydedilir. Hash değişirse audit yazılır.
- Borsa yine `-1013 Filter failure` döndürürse sonuç `ExchangeRejected` olur ve alarm üretilir (yerel kural eksik demektir).

## 3. Outbox ve dayanıklılık (ADR-0002; risk-model §4)

### 3.1 Gönderim
1. Emir niyeti ve `Submitting` olayı **tek transaction'da** yazılır.
2. İstek imzalanır (`timestamp = sunucu saati tahmini`, `recvWindow = 5000`) ve gönderilir.
3. Yanıta göre `Accepted`, `PartiallyFilled`, `Filled`, `Expired` ya da `ExchangeRejected` (hata kodu ve mesajıyla) yazılır. Dolumlar `trading.exchange_fills`'e `(symbol, tradeId)` benzersizliğiyle eklenir.
4. **HTTP 5xx, `-1007`, istemci timeout'u ya da yanıtsız bağlantı kopması** `Unknown` yazar. Borsa "execution status unknown" diyor ve bunun başarısızlık sayılmamasını istiyor (S-B12). **Tekrar gönderim yapılmaz.**
5. 4xx doğrulama hataları (`-1013`, `-2010` …) `ExchangeRejected` yazar. `-1021` (zaman) `ExchangeRejected` yazar ve saat ofseti yenilenir; aynı niyet bir sonraki döngüde yeni `attempt` ile denenebilir, çünkü emir motora hiç ulaşmamıştır.
6. 429'da `Retry-After` beklenir. 418'de kill switch otomatik açılır ve alarm üretilir (risk-model §3.1).

### 3.2 Exchange bakımı (her döngünün başında, A-5)
- Sunucu saati ofseti (`GET /api/v3/time`) hesaplanır.
- Terminal olmayan her emir `origClientOrderId` ile sorgulanır. Yeni dolumlar `myTrades` (`orderId` ile) üzerinden alınır.
- `Unknown` emirler §3.3'e göre çözülür.
- Koruyucu stop'u olmayan açık Testnet pozisyonları için stop yeniden denenir (§5.3).
- Açık emir listesi (`openOrders`) bilinen kimliklerle karşılaştırılır (§4.3).

### 3.3 `Unknown` çözümü ve `NeverAccepted` (A-3; S-B11)
- Sorgu emri bulursa gerçek durum yazılır.
- Sorgu `-2013 Order does not exist` dönerse:
  - borsa, `timestamp`'ı `recvWindow`'dan eski olan isteği Matching Engine'e iletmeden hemen önce yeniden kontrol edip reddeder (S-B11);
  - bu yüzden **sunucu saati ≥ `timestamp` + `recvWindow` + 60 sn** iken alınan **iki ardışık** `-2013` yanıtından sonra emir `NeverAccepted` olur. Kontrol, motor içindeki gecikmeleri kapsamadığı için 60 sn pay bırakılır.
- Yalnızca `NeverAccepted`'dan sonra aynı niyet `attempt + 1` ile yeni kimlik alabilir. Niyet artık geçerli değilse (ör. mum geçti, kill switch açıldı) yeni deneme yapılmaz.
- `Unknown` bir **giriş** emri çözülene kadar o sleeve'de yeni giriş açılmaz. Çıkış ve stop işlemleri sürer.
- **Restart:** açılışta `Submitting` durumunda kalmış emirler `Unknown` sayılıp aynı yoldan çözülür. Outbox, gönderimin olup olmadığını bilmez; kimlik deterministik olduğu için çift emir oluşmaz.

## 4. Testnet run'ı, ledger ve reconciliation

### 4.1 Run modeli (U-M6-4; A-4; ADR-0019 ile tutarlı)
- `trading.paper_runs.venue ∈ {paper, testnet}`.
- Bir kohortun ilk üyesi `TestnetActive` olduğunda o kohort için bir **Testnet run'ı** açılır. Sleeve yapısı paper run'ınınkiyle aynıdır (10.000 USDT nominal, eşit sleeve'ler). `TestnetActive` olmayan üyelerin sleeve'i girişe kapalıdır (`sleeve-not-testnet`). Sonradan aktive edilen üyenin sleeve'i açılır.
- **Paper run'ı değişmeden sürer.** `TestnetActive` üye paper'da aktif sayılır. Bugünkü kod `PaperTrading` dışındaki üyeleri devre dışı sayıyor; bu düzeltilir.
- Testnet run'ı aynı Kernel motorunu **harici dolum modunda** kullanır (ADR-0021 §3):
  - açılış dolumları ve mum içi stop simüle edilmez;
  - borsadan gelen, kaydedilmiş dolumlar uygulanır;
  - sinyaller, risk ve sizing kararları aynı koddan gelir.
- Adım günlüğü ve harici dolumlar kalıcı olduğu için restart'ta durum, M5'teki gibi baştan yeniden üretilir ve hash'le doğrulanır.

### 4.2 Ledger
- Her borsa dolumu ledger'a gerçek fiyat, miktar ve komisyonla yazılır. Komisyon varlığı (`commissionAsset`) base varlıksa pozisyon miktarı net tutulur. Testnet'te komisyon isteğe bağlıdır (S-B23); 0 olabilir.
- Sleeve başına nakit ≥ 0 ve pozisyon ≥ 0 invariant'ları M5'teki gibidir. Dust, ayrı bir alanda izlenir.

### 4.3 Reconciliation (her döngü)
1. **İç tutarlılık (M5):** ledger ↔ durum hash'i.
2. **Emirler:** borsadaki açık emirler ⊆ sistemin bilinen terminal olmayan emirleri (`pi-` öneki). Bilinmeyen `pi-` emri ya da bilinen bir emrin borsada kaybolması uyuşmazlıktır. `pi-` öneki dışındaki emirler (ör. kullanıcının elle verdiği emir) uyarı üretir.
3. **Bakiye:** her varlık için `free + locked − taban` = Σ Testnet run pozisyonları (tolerans: `stepSize` + dust). Taban (`trading.testnet_baselines`), Testnet run'ı açılırken alınan bakiye anlık görüntüsüdür.
- Uyuşmazlıkta yeni giriş açılmaz, alarm üretilir, manuel inceleme gerekir (risk-model §3.1). Çıkışlar ve stop'lar sürer.

### 4.4 Testnet sıfırlaması (A-6; S-B19)
- Testnet yaklaşık ayda bir, önceden bildirmeden sıfırlanır: emirler silinir, bakiyeler yenilenir, API anahtarları korunur.
- Belirti: bilinen açık emirlerin tamamının kaybolması ve bakiyenin tabandan sapması. Reconciliation `testnet-reset-suspected` alarmı üretir; yeni giriş bloke olur.
- **Kurtarma (kullanıcı):** `POST /testnet/rebaseline {confirm:true, reason}` (`ops_api`, audit). Mevcut Testnet run'ı `reset-lost` notuyla kapatılır; aynı sleeve yapısıyla yeni bir Testnet run'ı ve yeni taban açılır. Paper run'ı etkilenmez. Kayıp pozisyonlar L3'te "kesintiler" kategorisine girer.

## 5. Koruyucu stop, çıkışlar ve kill switch

### 5.1 Yerleştirme
- Giriş emri `Filled` ya da `PartiallyFilled` + `Expired` (IOC) olduğunda, dolan miktar için §2.2'deki stop emri **aynı döngüde ve outbox yolundan** gönderilir.
- Stop'un fiyatı ve miktarı, motorun o pozisyon için kaydettiği efektif stop ve borsadaki miktardır.
- Pozisyon, stop `Accepted` olana kadar **korumasızdır**.

### 5.2 Güncelleme
- Kapanışta stop seviyesi yükselirse (trailing, `StopRaised`) emir `POST /api/v3/order/cancelReplace`, `cancelReplaceMode=STOP_ON_FAILURE` ile yenilenir (S-B14).
- Kısmi başarısızlık (`-2021`, HTTP 409): iptal başarılı, yeni emir başarısızsa pozisyon korumasızdır (§5.3). İptal başarısızsa ve eski stop dolmuşsa, dolum işlenir.

### 5.3 Korumasız pozisyon (risk-model §4)
- Stop reddi, `Expired` ya da yerleştirme başarısızlığı durumunda:
  - pozisyon `unprotected` işaretlenir;
  - audit `protective_stop_failed` yazılır;
  - degraded nedeni `unprotected-stop` olur;
  - **tüm Testnet run'ında yeni giriş durur** (neden `unprotected`);
  - stop her bakım geçişinde yeniden denenir.
- **Güvenlik ağı:** korumasız bir pozisyonun mumu kapandığında `Low ≤ S` ise (kapanmış mum, canlı veri), bir sonraki döngüde `MARKET` çıkış gönderilir (neden `unprotected-stop`). Bu, paper'daki stop kuralının en yakın karşılığıdır; sapma L3'e yazılır.

### 5.4 Çıkış ve kapatma sırası
1. Pozisyonun stop emri iptal edilir (`DELETE /api/v3/order`). Stop açıkken satılacak base miktar kilitli olabilir; varsayım Kapı C'de gözlenir.
2. İptal `-2011` dönerse ya da stop dolmuşsa, dolum işlenir ve satış yapılmaz.
3. `MARKET` satış gönderilir.
4. Kalan miktar (kısmi dolum, `stepSize` üstü) varsa stop yeniden konur ve çıkış sonraki döngüde tekrar denenir.

Çıkışlar kill switch'ten, bayat veriden ve reconciliation uyuşmazlığından etkilenmez; her zaman denenir.

### 5.5 Kill switch (D11 rev.)
- Açıldığında:
  - yeni giriş niyeti üretilmez;
  - terminal olmayan **`entry`** emirleri iptal edilir;
  - **`stop` emirlerine dokunulmaz;**
  - bekleyen çıkışlar uygulanır.
- `POST /kill-switch` worker'a **anında bir bakım geçişi** sinyali gönderir; saatlik döngü beklenmez.
- "Tümünü kapat" ayrı manuel eylemdir ve §5.4 sırasını izler.

### 5.6 Bayat veri ve kesinti
- Bayat veride yalnızca çıkışlar çalışır. Stop emirleri borsada bekler (risk-model §4).
- Makine kapalıyken borsada tetiklenen stop'lar, açılıştaki bakımda dolum olarak alınır ve sırayla işlenen mumlara uygulanır.

## 6. Yaşam döngüsü: `PaperTrading → TestnetActive` (V10; D3, D30)

### 6.1 D30 koşulları (U-M6-3)
| Koşul | Tanım | Kaynak |
|---|---|---|
| Süre | `now − recorded_at(PaperTrading olayı)` ≥ 14 takvim günü | `research.lifecycle_events` |
| Mum | Stratejinin kohort paper run'ında işlenmiş (catch-up dahil) 4h mum sayısı ≥ 60 | `trading.steps` |
| Kritik hata (son 7 gün) | 0. Sayılanlar:<br>- `reconciliation_runs.ok = false` (ledger tutarsızlığı, invariant);<br>- duplicate dolum;<br>- o strateji için `strategy_hash_mismatch`;<br>- `ip-banned` (HTTP 418) | `trading.reconciliation_runs`, `trading.cycle_metrics`, `audit.events` |

İlerleme göstergesi ayrıca kesinti oranını verir (`trading.cycle_metrics`'teki kesinti aralıkları / takvim süresi). Bu oran bilgilendiricidir.

### 6.2 Geçiş
- **İstek:** `POST /strategies/{name}/testnet-activate {confirm: true, reason}`. Bearer token gerekir; `scripts/m6-testnet-activate.ps1` (kullanıcı) etkileşimli onay ister ve yönlendirilmiş girdiyi reddeder.
- **Kontroller ve yanıtlar:**
  - token yok ya da yanlış → 401;
  - `confirm` true değil → 400;
  - son durum `PaperTrading` değil (`Frozen`, `Candidate`, `Disabled` …) → 409;
  - D30 sağlanmıyor → 409; eksik koşullar listelenir;
  - Testnet etkin değil (`Trading:Testnet=disabled`) ya da anahtar yok → 409;
  - strateji hash'i manifestoyla uyuşmuyor → 409 (V14).
- **Başarıda tek transaction'da yazılanlar:**
  - `research.lifecycle_events`: `PaperTrading → TestnetActive`, aktör `ops_api`, kanıt `{d30: {...}, confirm: true, reason}`;
  - `trading.ops_actions` (`testnet-activate`);
  - `audit.events`.
- Testnet run'ı ve bakiye tabanı, bir sonraki bakım geçişinde Trading tarafından açılır.
- **DB trigger'ı:** `PaperTrading → TestnetActive` yalnızca kanıtta `d30` ve `confirm = true` varken kabul edilir. `trading_app` ve `research_app` bu tabloya bu geçişi yazamaz (yetki ya da trigger). `Frozen → TestnetActive` reddedilir.
- **Etiket:** ops tarafında `Validated` okunamaz. Strateji, `research.cohort_status`'ta kohortu `revealed` ve sonucu `Validated` olmadıkça `experimental` etiketi taşır. Bugün tüm stratejiler `experimental`'dır.
- `TestnetActive → Disabled` her zaman serbesttir (mevcut uç nokta). Pozisyonlar stop ve çıkış sinyalleriyle yönetilir.

## 7. Ops API ve dashboard (ADR-0020'nin uzantısı)
- **Yeni GET (salt okunur, token, güvenlik başlıkları, loopback `Host`):**
  - `/testnet`: run başına emir ve outbox durum sayıları; `Unknown` ve korumasız pozisyonlar; pozisyon başına borsadaki stop emrinin durumu (`Accepted` / yok / yedek tip); son reconciliation; sembol kuralları hash'i;
  - `/promotion`: strateji başına D30 ilerlemesi (`X/14 gün`, `Y/60 mum`, son 7 günde kritik hata, kesinti oranı), uygunluk ve `experimental` etiketi.
- **Yeni POST:** `/strategies/{name}/testnet-activate` (§6.2) ve `/testnet/rebaseline` (§4.4).
- Rota adları H24'ün yasaklı alt dizgilerini (`cohort`, `manifest`, `result`, `validat`) içermez. `OpsApi.Routes` pin listesi ve H24 testi bilinçli olarak güncellenir.
- **Dashboard:** "Testnet" sekmesi (emirler, outbox, stop'lar) ve genel bakışta D30 çubukları (`Kohort 0: X/14 gün, Y/60 mum`). CSP, `textContent` kuralı ve harici kaynak yasağı korunur. Dashboard'da eylem düğmesi yoktur. Doğrulama sentetik veriyle yapılır.

## 8. Güvenlik ve tatbikat

### 8.1 `TestnetGuardHandler` (D33)
- Şema `https`, host **yalnızca** `testnet.binance.vision`, varsayılan port.
- Yol ve metot allowlist'i:
  - `GET /api/v3/time`, `/exchangeInfo`, `/avgPrice`, `/account`, `/order`, `/openOrders`, `/myTrades`;
  - `POST /api/v3/order`, `/order/cancelReplace`;
  - `DELETE /api/v3/order`.
- Sembol allowlist'i: manifesto pariteleri.
- `AllowAutoRedirect=false`; her 3xx reddedilir. 418 → kill switch, 429 → `Retry-After`. Weight ve emir sayısı başlıkları izlenir.
- Base URL koda sabittir ve yapılandırmadan okunmaz. Handler zinciri: imzalama → guard → ağ. Guard son halkadır ve imzalı son URL'yi doğrular.
- Trading.Tests'e "testler ağa çıkmıyor" taraması eklenir.

### 8.2 Secret'lar
- `testnet_api_key` ve `testnet_api_secret` Docker secret'larıdır; yalnızca `trading-host` servisine bağlanır. Research.Host'ta ve evaluator'da bulunmaz; mimari ve compose testi bunu sabitler.
- Kullanıcı `scripts/m6-testnet-key.ps1` ile girer (`Read-Host -AsSecureString`, ekrana yazmaz).
- Anahtar oluştururken işlem izni açılır (varsayılan kapalıdır, S-B11). Testnet'te para çekme ve transfer yoktur (S-B19).
- Log redaction: dosyadan okunan secret'lar literal olarak maskelenir (ops token dahil). Ayrıca `signature=` ve `X-MBX-APIKEY` desenleri eklenir. Kanarya testi bunu sınar.

### 8.3 Tatbikat harness'ı (Kapı C; `Trading.Host testnet-drill`)
- Aynı image ve secret'larla, `scripts/m6-testnet-drill.ps1` üzerinden (kullanıcı) çalışır. Strateji run'larına bağlı değildir; `drill-` önekini kullanır. Testnet run'ı açılmadan, D30'dan bağımsız koşulur.
- **≥ 20 emirlik senaryolar:**
  1. `exchangeInfo`: manifesto sembollerinde filtrelerin varlığı ve `orderTypes` içinde `STOP_LOSS` sayımı.
  2. Hesap: `canTrade`, sunucu saati ofseti.
  3. Uzak `LIMIT GTC` → `NEW` → sorgu → iptal.
  4. `LIMIT IOC` alım → `FILLED` (ya da `EXPIRED`) → dolumlar.
  5. `STOP_LOSS` yerleştirme → sorgu → `cancelReplace` (yükseltme) → iptal.
  6. Aynı `newClientOrderId` ile açık emir varken ikinci gönderim → borsa reddi.
  7. Filtre ihlali (`LOT_SIZE`, `MIN_NOTIONAL`, `PRICE_FILTER`) → yerel red; ağa istek gitmez.
  8. İstemci tarafı timeout enjeksiyonu → `Unknown` → `origClientOrderId` ile çözüm (bulunursa durum, bulunmazsa §3.3 ufkundan sonra `NeverAccepted`).
  9. Kill switch: açık giriş emri iptal, stop emri borsada kalır.
  10. Tümünü kapat: stop iptal → `MARKET` satış.
  11. Kapanış: açık `drill-` emri 0, bakiye deltası tatbikat dolumlarıyla mutabık.
- **Claude Code'a görünen rapor (U-M5-5 ile aynı ilke):**
  - senaryo başına `PASS`/`FAIL` ve kısa neden kodu;
  - emir sayıları (tip × son durum);
  - hata kodu sayıları;
  - gecikme medyanı ve p95;
  - `Unknown` ve çözüm sayıları; duplicate emir sayısı;
  - `STOP_LOSS` destekli sembol sayısı / toplam;
  - `canTrade`; saat ofseti (ms);
  - USDT bakiyesinin run sermayesine yetip yetmediği (evet/hayır);
  - kural snapshot'ı hash'i.
  - **Fiyat, miktar, bakiye tutarı ve PnL yoktur.** Rapor SHA-256 ile özetlenir.

## 9. Kapı B test listesi (bağlayıcı)
- **Regresyon:**
  - mevcut testlerin tamamı;
  - M2'nin dört hash'i;
  - engine regresyonu (36 senaryo);
  - M3 golden testleri;
  - Kohort 0 ve Kohort 1 parity L1/L2 (simüle mod değişmez).
- **Guard:**
  - host, yol, metot ve sembol allowlist'i;
  - `api.binance.com`, `data-api.binance.vision` ve rastgele host'lar ağa gitmeden reddedilir;
  - 3xx reddi; 418 → kill switch; 429 → `Retry-After`;
  - Trading.Tests ağa çıkmama taraması.
- **İmza:**
  - Binance belgesindeki örnek vektörle HMAC-SHA256;
  - percent-encoding kuralı (S-B11);
  - saat ofseti ve `-1021` akışı;
  - imzalı URL ve anahtar loglarda maskeli (kanarya).
- **Filtreler** (property testleri):
  - yuvarlanmış miktar istenenden büyük olmaz;
  - fiyat ve stop yuvarlama yönleri;
  - her ihlal türünde handler'a giden istek sayısı 0.
- **Fault injection** (scripted handler):
  - timeout; 5xx; `-1007`; bağlantı kopması;
  - gecikmeli görünen emir (`-2013` ardından bulunma);
  - hiç ulaşmayan emir (`NeverAccepted`);
  - `-1021`;
  - `cancelReplace` `-2021`.
  - Her senaryoda **duplicate emir 0** (aynı niyet için borsaya ulaşan benzersiz emir ≤ 1) ve ledger reconcile.
- **Restart:** `Submitting` ve `Unknown` durumlarında process durur ve yeniden başlar. Aynı `clientOrderId` kullanılır, tek emir oluşur, durum hash'i aynı kalır.
- **Koruyucu stop:**
  - giriş dolumundan sonra gönderim (kısmi dolum dahil);
  - `STOP_LOSS` yoksa yedek tip;
  - red → alarm, `unprotected`, yeni giriş bloke, yeniden deneme;
  - güvenlik ağı çıkışı;
  - `cancelReplace` ile yükseltme;
  - çıkışta stop iptali yarışı (stop zaten dolmuş).
- **Kill switch (D11 rev.):** `entry` emirleri iptal, `stop` emirleri kalır; anında bakım sinyali; çıkışlar sürer.
- **Reconciliation:**
  - bilinmeyen `pi-` açık emri; kaybolan emir;
  - bakiye sapması; sıfırlama simülasyonu (`testnet-reset-suspected`);
  - yeniden taban (`confirm` zorunlu).
- **V10 ve D30:**
  - §6.2'deki tüm yanıt kodları;
  - DB trigger'ı (uygulama atlanarak doğrudan insert, `trading_app`/`research_app` yetkisi);
  - D30 hesaplayıcının sınır değerleri (13/14 gün, 59/60 mum, 7 günlük pencere sınırı, catch-up mumlarının sayılması);
  - `experimental` etiketi.
- **Paper gölge:** `TestnetActive` üyenin paper sleeve'i aktif kalır; paper L1/L2 değişmez.
- **Harici dolum modu:**
  - aynı mumlarla L1 sinyalleri paper ile aynı;
  - dolum ve stop'lar kaydedilmiş harici dolumlardan gelir;
  - L3'e Testnet kategorileri eklenir: gap ve slippage, stop-limit dolmaması, tick ve lot yuvarlaması, borsa reddi, IOC dolmaması, Testnet ↔ canlı fiyat farkı.
- **Ops API ve dashboard:**
  - yeni rotalar pin listesinde;
  - 401, 421 ve güvenlik başlıkları;
  - yanıtlarda secret yok;
  - statik dosya kuralları.
- **Mimari ve yapılandırma:**
  - Execution bağımlılıkları değişmez;
  - Testnet secret adları yalnızca Trading.Host/Execution kaynaklarında, compose'un `trading-host` servisinde ve `m6-*` script'lerinde;
  - research-host'ta Testnet secret'ı yok;
  - `HoldoutSecretReferenceTests` değişmez;
  - `RepositoryRuleTests` M6 deny kurallarını ve `CLAUDE.md` bölümünü kontrol eder.
- **Tatbikat:** sahte handler ile uçtan uca; rapor şemasında fiyat, miktar ve bakiye alanı yok.

## 10. Kapı C koşuları (kullanıcı)
1. **C1 – Anahtar:** testnet.binance.vision'da GitHub girişiyle HMAC anahtarı oluşturulur ve işlem izni açılır. `m6-testnet-key.ps1` çalıştırılır.
2. **C2 – Tatbikat:** `m6-testnet-drill.ps1` ile ≥ 20 emir. Rapor özeti ve SHA-256 paylaşılır. `sources.md`'deki varsayımlar (sembol bazında `STOP_LOSS`, bakiye kilidi, likidite, bakiye yeterliliği) güncellenir.
3. **C3 – Deploy:** `m6-update.ps1` sırayla şunları yapar:
   - migration 0009;
   - Trading.Host'un yeniden derlenmesi ve `PI_TRADING_TESTNET=enabled`;
   - `-ChecksOnly`: paper 3/3, reconciliation, Testnet bağlantısı, Testnet run'ı yok.
4. **C4 – Terfi:** D30 dolduğunda (en erken Kohort 0 için 2026-11-03, Kohort 1 için 2026-11-04) strateji başına `m6-testnet-activate.ps1 -Strategy <ad>` çalıştırılır. İlk Testnet döngülerinden sonra `-ChecksOnly` şunları gösterir:
   - korumasız pozisyon 0;
   - takılı `Unknown` 0;
   - reconciliation temiz.
   Ardından Testnet run'ında bir kill switch aç/kapat denemesi yapılır: stop emirleri kalır.
5. **C5 – Kabul:** `docs/m6-acceptance-report.md`, CHANGELOG ve roadmap.

## 11. Korkuluklar (T17; Kapı A'da uygulanır)
- **`.claude/settings.json` deny kuralları:**
  - `WebFetch(domain:testnet.binance.vision)`;
  - bu host için `curl`, `wget` ve PowerShell kalıpları;
  - `*-File*m6-*` (kullanıcı script'leri);
  - mevcut `docker compose*trading-host*` kuralı tatbikatı da kapsar.
- **`CLAUDE.md`:** §1'deki Claude Code kuralları.
- Testleri Kapı B'de yazılır (`RepositoryRuleTests`).

## 12. Kabul (roadmap M6'ya eşleme)
| Roadmap kriteri | Kanıt |
|---|---|
| Giriş dolumundan sonra koruyucu stop; kill switch'te giriş emirleri iptal, stop'lar kalır; stop reddi alarm ve giriş durdurma; Testnet stop sapmaları L3'te | §9 testleri; C2 senaryoları 5, 9, 10; C4 kill switch denemesi |
| Fault injection altında duplicate emir 0 ve ledger reconcile | §9 fault injection ve restart testleri; C2 senaryoları 6 ve 8 |
| Testnet'te ≥ 20 emrin tam yaşam döngüsü | C2 raporu |
| Filtre ihlali yapan emir borsaya gitmeden reddediliyor | §9 filtre testleri; C2 senaryosu 7 |
| Anahtar yalnızca Trading.Host'ta | §8.2; compose ve mimari testleri |
| `PaperTrading → TestnetActive` yalnızca `Frozen` soyundaki stratejiler için ve kullanıcı oturumuyla; doğrulama gerektirmiyor; `experimental` etiketi | §6; V10 testleri; C4 |
