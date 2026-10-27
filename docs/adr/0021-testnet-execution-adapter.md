# ADR-0021: Testnet execution — exchange adapter, outbox, harici dolum modu ve borsada koruyucu stop

- Durum: **Kabul edildi (Kapı A, kullanıcı onayı 2026-10-26)** · Tarih: 2026-10-26
- Karar türü: **U** (D33; `m6-protocol.md` U-M6-1…U-M6-6) ve **T** (adapter yapısı, outbox, Kernel harici dolum modu, guard, route değişikliği).
- Uygulama durumu: **Kapı B'de çevrimdışı uygulandı (2026-10-27; `verify.ps1` yeşil, 598 test).** Gerçek Testnet, Kapı C'de kullanıcının koşularıyla denenir.
- Uygulama notları (Kapı B):
  - **Kod yerleşimi:**
    - `Execution/Exchange`: `IExchangeAdapter`, `ExchangeFilters`, `ClientOrderIds`, `ExchangeStore` (outbox), `TestnetExecutor`, `TestnetDrill`, `ExchangeClock`;
    - `Exchange/Binance`: `TestnetGuardHandler`, `HmacSigningHandler`, `BinanceTestnetAdapter`;
    - Kernel: `PortfolioEngine` harici dolum modu (`PortfolioSetup.ExternalFills`, `StepContext.External`, `PendingOrders()`, `ProtectiveStopFor()`).
  - **Migration 0009:**
    - `paper_runs.venue`;
    - `run_closures`, `exchange_orders`, `exchange_order_events`, `exchange_fills`, `exchange_symbol_rules`, `testnet_baselines` (hepsi append-only);
    - "intent başına tek etkin emir" ve durum geçişi trigger'ları;
    - `lifecycle_rules()`'a V10 kuralı.
  - **Harici modda sıra:** sıra simüle modla aynıdır: açılış çıkışları → girişler → stop dolumları. Böylece aynı açılışta giren ve borsada stop'a takılan pozisyonun stop dolumu kaybolmaz; bu, eşdeğerlik testinde bulunan bir hataydı. Tek dolumlu giriş, fiyatı aynen alır (`x·q/q` decimal'de kesin değildir).
  - **Eşdeğerlik testi:** simüle motorun dolumlarıyla beslenen harici mod, her adımın durum hash'ini ve L1/L2 akışlarını birebir üretir. Tek fark açılıştaki dolmayan girişin kaydıdır: harici mod bunu `Canceled` ve borsa nedeniyle yazar.
  - **Dolumun adımı** (`exchange_fills.step_bar`), dolum kaydedilirken sabitlenir:
    - giriş, çıkış ve kapatma emirleri için karar mumu + 4h;
    - stop için tetiklendiği mum;
    - işlenmiş bir mumdan sonra bulunan dolum, sıradaki işlenmemiş muma yazılır.
    - Bir mumun adımı, o açılış için gönderilen emirler çözülene kadar bekler (`orders-pending`).
  - **Koruyucu stop:**
    - Giriş dolumundan hemen sonra, motorun bir sonraki adımda koyacağı seviyeyle (`ProtectiveStopFor`) gönderilir.
    - Stop miktarı, borsadaki holding'in lot adımına yuvarlanmış halidir.
    - Aynı karar mumunda yeniden yerleştirme gerekirse cancel-replace yerine önce iptal edilir, sonra yeniden konur; çünkü intent anahtarı aynıdır.
  - **Yeniden gönderim:** yalnızca `NeverAccepted` ile `-1021`/429 retleri yeni denemeyle tekrar gönderilebilir; bu retlerde emir eşleştirme motoruna hiç ulaşmamıştır (`TestnetExecutor.Retryable`).
  - **Girişe kapalı sleeve'in L2 nedeni:** `TestnetActive` olmayan üyenin Testnet run'ındaki sleeve'i protokolde `sleeve-not-testnet` olarak geçer; kodda mevcut `sleeve-disabled` nedeni kullanılır.
  - **Kill switch:** `POST /kill-switch`, `MaintenanceSignal` ile worker'ı uyandırır; worker'da her zaman tek bir bekleyen sinyal okuması tutulur, böylece istek kaybolmaz. HTTP 418, kill switch'i açar (`exchange_ip_banned`).
  - **Redaction:** dosyadan okunan secret'lar (Testnet anahtarı, Ops token, login parolaları) literal olarak maskelenir; `signature=` ve `X-MBX-APIKEY` desenleri eklendi.
  - **Sermaye (A-4 v1.1, U 2026-10-28):** Testnet run'ı `PaperRunConfig.ForTestnet` ile açılır: paper run'ın sleeve'leri, kohort başına 5.000 USDT nominal. Dashboard özet toplamları yalnızca paper run'larını içerir. Tatbikatın sermaye kontrolü `kohort × 5.000 × 0,80` ile ölçülür.
  - **`verify.ps1`:** test projeleri artık sırayla koşulur ve sayılar TRX dosyalarından toplanır. Projeler paralel koşulduğunda Testcontainers örnekleri üst üste biniyor ve 16 GB makinede bellek tükeniyordu (ADR-0013).
- İlgili: ADR-0002, ADR-0012, ADR-0013, ADR-0014, ADR-0015, ADR-0018, ADR-0019, ADR-0020; `m6-protocol.md`; `architecture.md` §5, §10; `risk-model.md` §2–§4; `sources.md` S-B11…S-B24; D3, D6, D9, D11, D25, D30, D33.

## Bağlam
- D6, borsadan bağımsız bir arayüzün arkasında Binance Spot Testnet ister. ADR-0014, Testnet'te koruyucu stop'un borsada emir olarak beklemesini ister. ADR-0002 ise emir gönderimi için transactional outbox ister.
- **M5'te emir modeli yoktur.** Dolumlar Kernel `PortfolioEngine.Step` içinde simüle edilir: açılışta `CostModel` v1, mum içinde OHLC stop kuralı (ADR-0018). Execution modülünün tek dış bağlantısı canlı mum kaynağıdır.
- Tüm `trading.*` tabloları append-only'dir (0007). Lifecycle trigger'ı `TestnetActive` geçişini henüz reddeder.
- Borsa belgeleri (S-B11, S-B12):
  - 5xx ve `-1007` yanıtlarında yürütme durumu **bilinmez** ve bu yanıtlar başarısızlık sayılmamalıdır;
  - açık bir emirle aynı `newClientOrderId` reddedilir;
  - `recvWindow`, isteğin Matching Engine'e iletilmesinden hemen önce yeniden kontrol edilir.
- Testnet yaklaşık ayda bir, önceden bildirilmeden sıfırlanır; yalnızca `/api` uç noktaları vardır (S-B19). Testnet fiyatları `≥ C` canlı veri sayılır ve Claude Code tarafından görülmez.
- **B6:** ev bağlantısının IP'si değişebildiği için anahtara IP kısıtı uygulanamaz.

## Karar

### 1. Borsadan bağımsız arayüz (Execution modülü)
- `IExchangeAdapter`:
  - emir verme, sorgulama (`clientOrderId`), iptal ve stop yenileme (`cancelReplace`);
  - açık emirler, dolumlar, bakiyeler, sembol kuralları ve sunucu saati.
- Sonuçlar borsa durumunu iç durum makinesine eşler (`m6-protocol.md` §2.1). Borsaya özgü tipler adapter'ın dışına çıkmaz.
- `ExchangeFilters` saf fonksiyonlardan oluşur: yuvarlama ve filtre kontrolü. İhlal eden emir ağa gitmeden `LocalRejected` olur.
- `BinanceTestnetAdapter` yalnızca REST kullanır; WebSocket user data stream'i kullanılmaz. Saatlik döngüde ve kill switch'teki anında bakımda polling yeterlidir. Makine kapalıyken borsada tetiklenen stop'lar açılışta dolum olarak alınır.
- Kod Execution modülünde kalır. Mimari kural (Execution → `{Kernel, MarketData}` ve Infrastructure) değişmez; yeni proje açılmaz.

### 2. Outbox ve append-only emir olayları (ADR-0002)
- Migration 0009 şu tabloları ekler:
  - `trading.exchange_orders`: niyet; `client_order_id UNIQUE`;
  - `trading.exchange_order_events`: durum olayları; geçiş kuralı DB trigger'ında;
  - `trading.exchange_fills`: `UNIQUE(symbol, trade_id)`;
  - `trading.exchange_symbol_rules`;
  - `trading.testnet_baselines`;
  - `trading.paper_runs.venue`.
- Emir niyeti ve `Submitting` olayı tek transaction'da yazılır, sonra gönderilir.
- `clientOrderId` deterministiktir: run, sleeve, sembol, karar mumu, amaç ve deneme numarasının hash'inden üretilir. Restart aynı kimliği üretir. Borsadaki aynı kimlik kuralı (S-B13) ikinci güvenlik katmanıdır.
- `Unknown` emir körü körüne tekrar gönderilmez. `origClientOrderId` ile sorgulanır. `NeverAccepted` kararı, `timestamp + recvWindow + 60 sn` ufkundan sonra alınan iki ardışık `-2013` yanıtına dayanır (S-B11). Ancak bundan sonra aynı niyet yeni bir deneme numarasıyla gönderilebilir.

### 3. Kernel: harici dolum modu
- `PortfolioEngine.Step`'e harici dolum modu eklenir. Bu modda:
  - açılış dolumları ve mum içi stop simüle edilmez;
  - adımın `StepContext`'inde taşınan, kaydedilmiş borsa dolumları uygulanır;
  - sinyaller, risk, sizing ve stop seviyelerinin hesabı aynı koddan gelir.
- Böylece Testnet run'ı L1 sinyallerinde paper ile aynı kalır. L2 kararları gerçek dolumların oluşturduğu portföy durumuna göre ayrışır; bu ayrışma L3'te kategorize edilir (ADR-0015).
- **Simüle mod varsayılandır ve davranışı değişmez.** Regresyon kilitleri:
  - M2'nin dört sonuç hash'i;
  - `engine-regression.v1.txt`;
  - M3 golden testleri;
  - Kohort 0 ve Kohort 1 parity L1/L2.

  Herhangi biri değişirse değişiklik reddedilir (ADR-0018 kuralı).
- Restart'ta durum, adım günlüğü ve kaydedilmiş harici dolumlardan baştan yeniden üretilir ve hash'le doğrulanır (ADR-0018 §1).

### 4. Testnet run'ı ve paralel paper (ADR-0019 ile tutarlı)
- Kohort başına ayrı bir Testnet run'ı açılır. Sleeve yapısı paper run'ınınkiyle aynıdır. `TestnetActive` olmayan üyelerin sleeve'i girişe kapalıdır.
- Paper run'ı değişmeden sürer (U-M6-4). `TestnetActive` üye paper'da aktif sayılır.
- Reconciliation üç katmanlıdır:
  1. iç ledger;
  2. borsa emirleri;
  3. varlık bakiyesi − aktivasyon tabanı.
- Testnet sıfırlaması reconciliation'da tespit edilir. Kurtarma, kullanıcının onaylı "yeniden taban" eylemidir (`m6-protocol.md` §4.4).

### 5. Koruyucu stop ve kill switch (ADR-0014, D11 rev., D25)
- **Stop yaşam döngüsü:**
  - Giriş dolumundan sonra aynı döngüde `STOP_LOSS` SELL emri gönderilir. Sembolde `STOP_LOSS` yoksa bantlı `STOP_LOSS_LIMIT` kullanılır.
  - Stop seviyesi yükseldiğinde `cancelReplace` (`STOP_ON_FAILURE`) kullanılır.
  - Stop reddedilirse pozisyon "korumasız" sayılır: alarm üretilir, Testnet run'ında yeni giriş durur ve stop her döngüde yeniden denenir.
  - Korumasız pozisyonun kapanan mumunda `Low ≤ S` ise `MARKET` çıkış gönderilir (güvenlik ağı).
- **Kapatma sırası:** önce stop iptal edilir, sonra `MARKET` satış yapılır. Stop zaten dolduysa satış yapılmaz.
- **Kill switch:** açık **giriş** emirlerini iptal eder, stop emirlerine dokunmaz. `POST /kill-switch` worker'da anında bir bakım geçişi tetikler.

### 6. Güvenlik (D9, D33)
- **`TestnetGuardHandler`** (`LiveKlineGuardHandler` deseni):
  - yalnızca `https://testnet.binance.vision`, varsayılan port;
  - yol, metot ve sembol allowlist'i;
  - 3xx reddi; 418'de kill switch; 429'da `Retry-After`.
  - Base URL koda sabittir, yapılandırmadan gelmez.
  - Zincir sırası: imzalama → guard → ağ. Guard son halkadır.
- **İmzalama:** HMAC-SHA256 (U-M6-6). BCL'de vardır, yeni paket gerekmez.
- **Anahtar:** yalnızca `trading-host` servisinin Docker secret'larında bulunur (`testnet_api_key`, `testnet_api_secret`).
- **Log redaction:** dosyadan okunan secret'lar literal olarak maskelenir; `signature=` ve `X-MBX-APIKEY` desenleri eklenir.
- **D33:** IP kısıtının yerini host allowlist'i, redirect reddi, secret izolasyonu ve işlem izni alır. Karar yalnızca Testnet içindir.
- trading-host'un `ops` ağı `internal` değildir; ağ düzeyinde egress kısıtı yoktur. Sınır koddaki guard'dır (M5'teki canlı mum istemcisiyle aynı model). Bu, kabul edilmiş artık risktir.

### 7. Yaşam döngüsü (V10; D3, D30)
- `PaperTrading → TestnetActive` yalnızca Ops API'nin `POST /strategies/{name}/testnet-activate {confirm: true}` yoluyla yapılır. Bu yol bearer token ister (kullanıcı oturumu) ve D30'u sunucuda hesaplar.
- `lifecycle_rules()` bu geçişi yalnızca kanıtta `d30` ve `confirm = true` varken kabul eder. `trading_app` ve `research_app` bu geçişi yazamaz.
- `Frozen` soyu, mevcut zincir kuralıyla zaten zorunludur (`Frozen → PaperTrading → TestnetActive`).
- **Route değişikliği:** M9a'nın "yeni POST yok" kuralı yalnızca dashboard'a aittir (ADR-0020). M6 bilinçli olarak iki POST ekler: `testnet-activate` ve `/testnet/rebaseline`. Ayrıca iki salt-okunur GET ekler: `/testnet` ve `/promotion`. Pin listesi ve H24 testi bu dört rotayla güncellenir; yasaklı alt dizgi kuralı aynen kalır.

### 8. Kullanıcı tatbikatı
- `Trading.Host testnet-drill` alt komutu aynı image ve secret'larla çalışır; `drill-` önekini kullanır ve strateji run'larından bağımsızdır. ≥ 20 emrin tam yaşam döngüsünü dener.
- Rapor yalnızca operasyonel alanlar içerir; fiyat, miktar ve bakiye tutarı yoktur (`m6-protocol.md` §8.3).

## Gerekçe
- **Kernel'de harici dolum modu:** ikinci bir motor yazmak yerine aynı `Step` kodu kullanılır. Böylece sinyal ve risk kararları paper, backtest ve Testnet'te tek koddan gelir (ADR-0018'deki "tek motor, iki çağırma biçimi" ilkesi). Borsadan gelen gerçek dolumlar, simülasyonun yerini yalnızca dolum noktasında alır.
- **Deterministik kimlik ve append-only olaylar:** restart, timeout ve 5xx belirsizliğinde çift emir üretmemenin en basit yolu, aynı niyetin her zaman aynı kimliği üretmesidir. `NeverAccepted` kuralı, borsanın belgelenmiş `recvWindow` kontrolüne dayanır.
- **`STOP_LOSS` (market):** gap'te çıkışı garanti eder ve backtest'in "Open'dan çıkış" kuralına en yakın davranıştır. `STOP_LOSS_LIMIT` dolmayabilir (S-B18).
- **REST polling:** saatlik 4h döngüsünde WebSocket'in getireceği gecikme kazancı küçüktür. Bağlantı yönetimi, yeniden bağlanma ve listen token yaşam döngüsü ise ek bir hata yüzeyi açar.
- **Paralel paper:** D30 geçmişi ve paper ↔ Testnet L3 karşılaştırması korunur. Mevcut run'lar değişmez (ADR-0019).

## Sonuçlar
- Execution'a emir modeli, adapter, filtreler, outbox ve Testnet trader eklenir. Kernel'e harici dolum modu eklenir. Migration 0009 gelir. Trading.Host'a Testnet yapılandırması ve tatbikat komutu eklenir. Dashboard'a "Testnet" sekmesi ve D30 göstergesi eklenir.
- Testnet sonuçları performans kanıtı değildir ve doğrulamaya girmez (V2). Stratejiler `experimental` etiketi taşır.
- Aylık Testnet sıfırlaması, 30 günlük provada (M10) en az bir "yeniden taban" eylemi gerektirebilir.
- Evaluator ve `*Holdout*` dosyaları değişmez.

## Alternatifler
| Alternatif | Neden reddedildi |
|---|---|
| OTO/OTOCO listeleri (giriş + stop tek istekte; S-B22) | Bekleyen stop, giriş **tamamen** dolunca konur; IOC kısmi dolumda stop hiç konmaz. Liste kimlikleri ve durum eşlemesi ek karmaşıklık getirir. Ayrı stop emri ADR-0014'teki akışla aynıdır |
| `STOP_LOSS_LIMIT`'i her yerde kullanmak | Gap ya da sığ defterde dolmayabilir; "çıkışlar her zaman çalışır" ilkesini zayıflatır |
| WebSocket user data stream | Bağlantı ve token yönetimi yükü; saatlik döngüde gereksiz (YAGNI) |
| Ed25519 imza | Belgenin önerisi, ama ek kripto bağımlılığı ve anahtar dosyası yönetimi gerektirir. REST'te HMAC yeterli (U-M6-6) |
| Testnet için ayrı motor ya da stratejiyi Testnet'e taşıyıp paper'ı durdurmak | Parity zayıflar; D30 geçmişi ve L3 karşılaştırması kaybolur (U-M6-4) |
| Ağ düzeyinde egress kısıtı (ayrı Docker ağı ve proxy) | Ek container ve bellek. M5'teki canlı mum istemcisiyle aynı kod düzeyi guard modeli korunur; yeniden değerlendirme M10'da |
| Testnet anahtarına IP kısıtı | Dinamik ev IP'sinde uygulanamaz (B6 → D33) |
