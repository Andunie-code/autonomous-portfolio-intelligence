# ADR-0018: Trading runtime, adım adım portföy motoru ve paper execution

- Durum: **Kabul edildi (Kapı A, kullanıcı onayı 2026-10-20)** · Tarih: 2026-10-19
- Karar türü: **T** (teknik lider). Kullanıcı kararları `m5-protocol.md` §0'da.
- Uygulama durumu: **Kapı B'de çevrimdışı uygulandı (2026-10-20); gerçek ortam Kapı C'de.**
- Uygulama notları (Kapı B):
  - `PortfolioEngine` mutable bir nesnedir: `Step(t, bars, reference, context)` durumu yerinde ilerletir ve olayları biriktirir. Bu, §1'deki `Step(state, …) → (state, events)` biçimine işlevsel olarak eşdeğerdir. Restart'ta durum, `trading.steps`'teki adım girdileriyle (sembol listesi, bağlam, `RiskConfig` sürümü) baştan yeniden üretilir ve her adımın durum hash'i karşılaştırılır.
  - `BacktestEngine.Run` tek sleeve'li bir `PortfolioReplay` döngüsüdür. M2'nin dört hash'i (`m3-parity.ps1`, gerçek `R`) ve motor değişikliğinden önce yakalanan 36 sentetik senaryo hash'i (`engine-regression.v1.txt`) birebir üretilir.
  - Ayrı bir `RiskEngine` sınıfı yerine risk ve sizing kararları motorun adımında, L2 olayı olarak kaydedilir. Neden kodları: `max-positions`, `no-volatility`, `total-exposure`, `asset-cap`, `experimental-cap`, `cluster-cap`, `min-notional`, `daily-loss`, `drawdown-halt`, `kill-switch`, `stale-data`, `reconciliation`, `sleeve-disabled`, `close-all`.
  - "Tümünü kapat" aynı açılıştaki bekleyen girişleri de iptal eder (`close-all`). Aksi halde kapatmanın hemen ardından yeni pozisyon açılabiliyordu.
  - Operasyonel eylemler, Ops API'ye geliş zamanından bağımsız olarak işlenen bir sonraki adımda uygulanır. Uygulandıkları adım `lastActionId` ile kaydedilir.
  - Ledger nakdi motor nakdiyle 1e-12 USDT toleransla mutabık tutulur. Motor nominal + ücreti tek işlemde, ledger iki satırda düşer; 28. basamakta yuvarlama farkı oluşabilir.
  - `PaperTrader`, aynı process içinde adım günlüğü değişmediyse motoru önbellekte tutar (replay yok). Yeni bir örnek restart demektir. Parity replay bellek içi bir store (`InMemoryPaperStore`) ile aynı trader kodunu kullanır.
- İlgili: ADR-0001, ADR-0002, ADR-0011, ADR-0012, ADR-0013, ADR-0014, ADR-0015; `architecture.md` §5, §6.4, §7; `risk-model.md` §2–§4; `m5-protocol.md`

## Bağlam
- Backtest motoru (`BacktestEngine.Run`) yalnızca batch çalışır. Nakit, pozisyonlar, bekleyen emirler, zirve, gün başı ve halt gibi tüm durum yerel değişkenlerde tutulur. Paper trading'in aynı kararları mum mum, restart'lar arasında üretmesi gerekir (L1/L2 parity, ADR-0015).
- Risk limitleri `RiskSettings` içinde ve motorun gövdesinde dağınıktır. `ExperimentalPairsMaxExposure` (D22), veri tazeliği (D7) ve parite kategorisi kodda yoktur.
- `Execution` modülü, `Trading.Host` ve `trading` şeması boştur.

## Karar

### 1. Kernel: adım adım çalışan `PortfolioEngine`
- `BacktestEngine.Run` içindeki durum açık ve serileştirilebilir bir `PortfolioState` olur: sleeve başına nakit, pozisyonlar, bekleyen giriş ve çıkışlar; portföy düzeyinde zirve, gün başı değeri, giriş yasağı bitişi ve halt; sembol başına gösterge durumu, geçerli mum sayısı ve gün sonu kapanışları.
- `PortfolioEngine.Step(PortfolioState, BarSlice, StepContext) → (PortfolioState, IReadOnlyList<EngineEvent>)`. `BarSlice` bir 4h zaman damgasındaki tüm mumlardır. `StepContext` tazelik bayrağı, kill switch, devre dışı sleeve'ler ve manuel kapatma isteklerini taşır.
- `BacktestEngine.Run`, `Step` üzerinde bir döngüdür. Tek sleeve, 10.000 USDT ve operasyonel bayraklar kapalıyken çıktısı ve sonuç hash'i **değişmez** (M2'nin dört hash'i regresyon kilididir).
- Gösterge durumu (`IStrategyInstance`) serileştirilmez. Restart'ta, kayıtlı mumlardan deterministik olarak yeniden üretilir; bu, durum hash'iyle doğrulanır. Böylece DSL ve kodlanmış stratejilere serileştirme yükü eklenmez.
- `EngineEvent` L1 ve L2 olaylarını taşır (`m5-protocol.md` §5.1). Kanonik biçim ve hash Kernel'dedir.

### 2. Kernel: `RiskConfig` v2 ve `RiskEngine`
- `RiskConfig` = `RiskSettings` alanları + `ExperimentalPairsMaxExposure` (varsayılan 0,10) + `MaxDataLagBars` (2) + `ClusterCap` (varsayılan **kapalı**; yalnızca P1 analizinde kullanılır, bağlayıcı hale getirilmesi kullanıcı kararıdır).
- `RiskEngine.Evaluate(intents, state, config, categories) → RiskDecision[]` (Approved, Clipped, Rejected ve nedeni). Motorun gövdesindeki sizing ve limit kodu buraya taşınır. Sıralama deterministiktir: sleeve sırası, sonra çekirdek rank.
- Deneysel tavan ve tazelik, yalnızca deneysel parite ya da bayat veri varken etkili olur. Kohort 0'ın tüm pariteleri çekirdek olduğu ve backtest verisi tam olduğu için M2 hash'leri değişmez.
- `RiskConfig` sürümlüdür. Trading'de etkin sürüm `trading.risk_config_versions`'tan okunur; ilk sürüm Kohort 0 manifestosunun risk bloğu ve D22 tavanıdır.

### 3. Kernel: küçük manifesto okuyucu
Trading, HoldoutEvaluator ve Research'e referans veremez (mimari kural). `cohort-0.json` şemasının Trading için gereken alt kümesi (stratejiler, hash'ler, pariteler ve kategorileri, aralık, risk, sermaye) Kernel'de, bağımlılıksız bir okuyucuyla ayrıştırılır. Evaluator kendi `ManifestParser`'ını kullanmaya devam eder. Manifesto hash'i her iki tarafta aynı kanonik kuralla hesaplanır; bir test bu eşitliği sabitler.

### 4. Execution modülü ve Trading.Host
- **`Execution`:**
  - `PaperCycle`: döngü adımları, `m5-protocol.md` §2.1;
  - `PaperExecution`: kernel kurallarıyla dolum;
  - `Ledger` ve `Reconciliation`;
  - `TradingKillSwitch` (D11);
  - `OperationalActions`, `LiveCandleFeed` portu, `StrategyLoader` (`StrategyHashGuard`), `CorrelationMonitor`.
  - Kalıcılık Npgsql ile; tek transaction'da ledger, olaylar, durum hash'i ve audit.
- **`Trading.Host`:** `TimeProvider`'lı worker ve Ops API (`127.0.0.1:55490`, bearer token). İki DB kimliği kullanır: döngü için `trading_app`, `RiskConfig` ve operasyonel eylem yazımı için `ops_api`.
- **Canlı mumlar:** `MarketData` modülünde `LiveKlineClient`, kendi guard handler'ı ve `LiveCandleStore` (`market.live_candles`). Arşiv tarafındaki `ArchiveGuardHandler` ve `market.candles` kesim `CHECK`'i değişmez.
- **Bağımlılıklar:** Execution → Kernel, Infrastructure, MarketData. Trading.Host → Execution. Research, Researcher, Ai ve HoldoutEvaluator'a referans yoktur (mimari test).

### 5. Lifecycle ve dondurulmuş strateji kayıtları
`research.frozen_strategies` ve `research.lifecycle_events` append-only tablolardır (`m5-protocol.md` §4.5). `Frozen → PaperTrading` geçişini yalnızca parity replay aracı (BacktestCli `parity-m5`, `research_app`) başarı kanıtıyla yazar. Trading yalnızca okur.

### 6. Paper sermayesi ve sleeve'ler
Önerilen model (`m5-protocol.md` §2.2, A-1): 10.000 USDT aktif stratejiler arasında eşit sleeve'lere bölünür; D7 limitleri portföy genelinde uygulanır. Çok sleeve'li hesap Kernel motorundadır. Böylece paper ile kernel replay'i arasında L1/L2 birebir kalır; izole evaluator simülasyonuyla farklar L3'tedir.

### 7. Ops ve holdout tarafının yeniden düzenlenmesi
`ops` = yeniden adlandırılmış `portfolio_dev` (U-M5-2). Roller, `pg_hba`, ağlar ve statik IP'ler için bkz. ADR-0013 rev. notu (2026-10-19) ve `m5-protocol.md` §4.

## Gerekçe
- Tek motor, iki çağırma biçimi: backtest ve paper aynı `Step` kodunu kullandığı için parity, ayrı bir yeniden uygulamanın eşitliğine değil, aynı kodun aynı girdilerle çağrılmasına dayanır. Parity replay, Trading'in veri besleme, kalıcılık ve restart katmanlarının bu eşitliği bozmadığını gösterir.
- Gösterge durumunu serileştirmek yerine mumlardan yeniden üretmek, DSL'e ve iki baseline'a dokunmadan restart determinizmini sağlar. Maliyeti, açılışta ısınma kadar mumun yeniden işlenmesidir (Kohort 0: 20 parite × birkaç yüz mum).
- Risk motorunu Kernel'e almak, L2 kararlarının backtest, paper ve P1 analizinde aynı olmasını sağlar.

## Sonuçlar
- `BacktestEngine`'in iç yapısı değişir. Bu, sonucu etkileyen bir engine değişikliği değildir ve M2 hash'leriyle gösterilir. Hash değişirse değişiklik reddedilir.
- Yeni projeler: `tests/Trading.Tests`. `Execution` ve `Trading.Host` doldurulur.
- Bilinen sınırlama: paper, tick izlemediği için mum içi stop'u kapanışta, OHLC kuralıyla uygular (ADR-0014). Gerçek zamanlı koruma Testnet'te (M6), borsadaki stop emriyle sağlanır.

## Alternatifler
| Alternatif | Neden reddedildi |
|---|---|
| Trading'de motoru yeniden yazmak | İki uygulama arasında parity'yi test etmek, tek uygulamayı iki biçimde çağırmaktan daha zayıftır |
| Gösterge durumunu serileştirmek | Her gösterge ve DSL düğümü için kalıcı biçim; sürüm uyumluluğu yükü |
| Strateji başına ayrı 10.000 USDT | Toplam nominal sermaye 20.000 olur; D7 portföy limitleri anlamını yitirir |
| Risk kontrolünü yalnızca Trading'de yapmak | L2 parity'si ve P1 analizi aynı kodu kullanamaz |
