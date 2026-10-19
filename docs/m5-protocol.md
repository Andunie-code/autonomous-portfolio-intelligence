# M5 Protokolü — Yerel holdout ortamı ve deterministik paper trading (v1)

- Durum: **Ön kayıt — kullanıcı tarafından onaylandı (Kapı A, 2026-10-20, U; A-1…A-6 ve E1–E10 dahil).** Ön kayıt kanıtı, bu belgenin eklendiği git commit'idir. O commit'ten önce M5 kodu yazılmaz; parity replay, P1 analizi ve paper çalışması koşulmaz.
- Karar türleri: **U** = kullanıcı, **T** = teknik lider, **P** = öneri (kullanıcı onayı gerekir).
- İlgili: ADR-0012, ADR-0013 (rev. notu 2026-10-19), ADR-0014, ADR-0015, ADR-0018; `architecture.md` §1, §5, §6.2–§6.5, §10–§12; `risk-model.md` §2–§4; `testing-strategy.md` §5, §7; `m2-protocol.md` v1; `m3-protocol-v2.md`; `docs/cohorts/cohort-0.json`.
- Değiştirilmeyenler: M2 maliyet modeli v1, allocation v1, D25 hard stop, Kohort 0 manifestosu, Protokol v2 kapıları, M2'nin dört sonuç hash'i.

## 0. Kullanıcı kararları

### 0.1 Alınmış kararlar (U, 2026-10-19)
| # | Karar |
|---|---|
| U-M5-1 | M5 başlatıldı. Kapılar: **A** = bu belge, ADR-0018, ADR-0013 notu, `sources.md`, korkuluklar; **B** = çevrimdışı kod ve testler; **C** = gerçek yerel Docker ortamı (H-ENV, Kohort 0 aktarımı, parity replay, paper çalışması, yedek, P1 raporu) |
| U-M5-2 | `portfolio_dev` yedeklenir ve **`ops` olarak yeniden adlandırılır.** Deneme sayacı, deneyler, kayıtlar ve AI ledger tek yetkili yerde kalır. Kapı C'den sonra Claude Code loopback'ten **yalnızca `research_app`** login'iyle bağlanır; owner, migration, trading ve yedek işlemleri kullanıcının script'leriyle yapılır |
| U-M5-3 | Makine dışı yedek **harici disk/USB'dir.** Script şifreli dosyayı repo ve OneDrive dışındaki yerel bir klasöre yazar; harici diske kopyalamayı kullanıcı yapar |
| U-M5-4 | Evaluator ve adında `Holdout` geçen dosyalarda yalnızca **§10'daki liste** değişir (bu belgenin onayıyla). Liste dışı her değişiklik ayrı onay ister |
| U-M5-5 | Paper raporlarında Claude Code **yalnızca operasyonel metrikleri** görür (§7.3). Fiyat, PnL ve işlem ayrıntısını kullanıcı Ops API'den görür |

### 0.2 Kapı A kararları (U, 2026-10-20: tümü öneri olarak onaylandı)
| # | Konu | Öneri | Bölüm |
|---|---|---|---|
| A-1 | Paper sermayesinin stratejiler arasında paylaşımı | 10.000 USDT, aktif strateji başına eşit "sleeve"; D7 limitleri portföy genelinde | §2.2 |
| A-2 | Kohort 0'ın gerçek evaluator release'ine bağlanması | Audit'li engine eşdeğerliği kaydı (M2 hash'leri birebir) | §6.3 |
| A-3 | Canlı mum kaynağı ve Trading.Host'un ağ erişimi | `data-api.binance.vision` `/api/v3/klines`, yalnızca Trading.Host. Gerçek erişim Kapı C'de, ayrıca kullanıcı onayıyla | §2.3 |
| A-4 | P1 analizinin ön kaydı ve öneri kuralı | §9 | §9 |
| A-5 | `holdout_synth` veritabanı | Silinmez; `CONNECT` kapatılır. Silinmesi ayrı karar | §4.1 |
| A-6 | Evaluator ve `*Holdout*` değişiklik listesi | §10 | §10 |

## 1. Kapsam ve sınırlar
- **Kohort 0** (ema-trend ve supertrend, 20 çekirdek parite, 4h; `cohort-0.json`) M5'te paper'a giren tek kohorttur.
- M5'te **Gemini çağrısı yoktur** (0 USD). Allocation, varsayılan deterministik politikadır (T11).
- **Holdout penceresi verisi import edilmez.** Kohort 0'ın ileriye dönük penceresi 2027-10-17'de kapanır. Evaluator'ın arşiv importçusu M5'te yalnızca çevrimdışı testlerle doğrulanır. Tanısal değerlendirme (K_D) M5'te yapılmaz.
- **Claude Code** `≥ C` verisini indirmez, okumaz, incelemez. Holdout DB'ye ve evaluator'a bağlanmaz. H-ENV'i, bootstrap'i, deploy'u, paper başlatmayı ve yedeği kendisi koşmaz; bu script'ler deny kurallarıyla engellenir (§11). Açığa çıkmamış Kohort 0'ın paper sonuçları araştırma girdisi değildir.

## 2. Paper trading

### 2.1 Döngü (architecture §5; T)
- Trading.Host sürekli çalışır. Döngü her saat `HH:00:15` UTC'de başlar (`TimeProvider`; enjekte edilebilir).
- Strateji aralığı 4h'tir. Stop kuralı, sinyaller ve kararlar **yalnızca kapanmış 4h mumlarında** çalışır; aradaki saatlik döngüler tazelik, reconciliation ve sağlık kontrolü yapar. Bu, backtest ile parity'nin şartıdır (ADR-0014 §3).
- Bir 4h mumunun işlenme sırası (backtest `Run` ile aynı):
  1. veri çekme ve doğrulama (§2.3);
  2. reconciliation (§2.5); tutarsızlıkta yeni giriş yok, alarm;
  3. açılış: bekleyen çıkışlar, ardından bekleyen girişler, `t` açılışında doldurulur (`CostModel` v1);
  4. mum içi koruyucu stop: `Open ≤ S` → `Open`; değilse `Low ≤ S` → `S`; giriş mumunda yalnızca `Low` kuralı; stop slippage'ı;
  5. kapanış: göstergeler, günlük kayıp ve drawdown, çıkış sinyalleri, trailing stop güncellemesi, giriş adayları, allocation ve risk; emirler `t+1` açılışında uygulanır;
  6. ledger, audit ve durum anlık görüntüsü tek transaction'da yazılır.
- **Isınma (backfill):** ilk açılışta parite başına son **45 günün** (270 adet 4h mum) kapanmış mumları çekilir. EMA100 101 mum, volatilite 31 gün sonu kapanışı ister. Paper'ın işlem başlangıcı `T0` (ilk açılıştan sonraki ilk 4h sınırı) kaydedilir; `T0`'dan önce giriş yapılmaz (backtest'teki `TradingStart` ile aynı anlam).
- **Bayat veri (D7):** son kapanmış 4h mumu beklenenden **2 mumdan fazla** gecikmişse (beklenen kapanıştan 8 saatten fazla) yeni giriş açılmaz; çıkışlar ve stop kuralı çalışır. Eksik mumlar geldiğinde sırayla işlenir. Bir sembolde boşluk varsa backtest'teki boşluk kuralı uygulanır (göstergeler sıfırlanır, `WarmupBars` dolana kadar sinyal yok).
- **Restart (risk-model §4):** açılışta önce son durum anlık görüntüsü ledger'dan yeniden üretilerek doğrulanır (reconciliation), sonra kaçırılan kapanmış mumlar sırayla işlenir (stop kuralı dahil). Kesinti aralıkları `trading.cycle_metrics`'e yazılır.

### 2.2 Sermaye ve sleeve'ler (A-1, öneri)
- Paper sermayesi **10.000 USDT** nominaldir (requirements §4). Aktif `PaperTrading` stratejileri arasında **eşit sleeve'lere** bölünür (Kohort 0: 2 × 5.000 USDT). Sleeve içi hesap, backtest motorunun tek strateji hesabıyla aynıdır.
- **D7 limitleri portföy geneli içindir** ve sleeve'ler üzerinde deterministik sırayla uygulanır: kohort üye sırası, sonra çekirdek rank.
  - en fazla 5 açık pozisyon (toplam);
  - varlık başına %20 ve toplam %80 (sleeve'ler toplamı, portföy değerine göre);
  - günlük kayıp %3 ve drawdown %15 portföy değerinde; aşılırsa tüm sleeve'lerde yeni giriş durur;
  - `ExperimentalPairsMaxExposure` %10 portföy değerinde.
- Aynı varlığı iki sleeve tutabilir. Varlık tavanı toplama uygulanır.
- **Parity'ye etkisi:** portföy kısıtları kernel'deki çok sleeve'li motorda da aynen uygulanır, dolayısıyla L1/L2 birebir kalır. Paper ile evaluator'ın izole simülasyonu arasındaki farklar L3'te "ortak sermaye ve portföy kısıtları" kategorisine girer (ADR-0015).
- Alternatif (önerilmez): strateji başına ayrı 10.000 USDT. Toplam nominal sermaye 20.000 USDT olur ve D7 portföy limitleri anlamını yitirir.

### 2.3 Canlı mum kaynağı (A-3; `sources.md` S-B9, S-B10)
- **Kaynak:** `GET https://data-api.binance.vision/api/v3/klines`. Resmi belge, yalnızca public market data gönderen API'ler için bu base endpoint'i önerir (S-B10). Anahtar gerekmez. İstek weight'i 2, `limit` en fazla 1000; varsayılan zaman birimi ms.
- **Yalnızca Trading.Host** erişir ve veriyi yalnızca `market.live_candles`'a yazar. Guard handler:
  - host allowlist'i yalnızca bu host; yol yalnızca `/api/v3/klines`;
  - redirect reddi; `symbol` yalnızca manifesto paritesi; `interval=4h`; `startTime`/`endTime` zorunlu;
  - `X-MBX-USED-WEIGHT-*` takibi; 429'da `Retry-After`; 418'de otomatik halt ve alarm (risk-model §3.1).
- **Kapanmış mum kuralı:** belge, son mumun kapanmamış olabileceğini açıkça söylemiyor (S-B9). Bu yüzden yalnızca `closeTime < now` olan ve `closeTime = openTime + 4h − 1 ms` eşitliğini sağlayan mumlar kabul edilir. Mum açılışı 4h ızgarasına hizalı olmalıdır.
- **Doğrulama:** M1 OHLC kuralları. Aynı `open_time` için sonradan farklı bir değer gelirse ilk kayıt korunur (append-only), fark anomali olarak kaydedilir ve L3'te "canlı/arşiv mum farkı" kategorisine girer.
- **Bütçe:** saatlik döngüde 20 istek × 2 weight. Backfill parite başına tek istektir.

### 2.4 Operasyonel güvenlik eylemleri (D11, D23, V9; T)
| Eylem | Davranış | Parity |
|---|---|---|
| Trading kill switch | Yeni giriş yok. Bekleyen (`t+1`) girişler iptal edilir. Pozisyonlar ve **koruyucu stop kuralı sürer**. Bekleyen çıkışlar uygulanır. Kapatma yalnızca manuel | Kapsam dışı (operasyonel) |
| Strateji devre dışı bırakma (`* → Disabled`) | O sleeve'de yeni giriş yok ve bekleyen girişler iptal; mevcut pozisyonlar stop ve çıkış sinyalleriyle yönetilir | Kapsam dışı |
| Tümünü kapat / pozisyon kapat | `Manual` nedenli çıkış emri kuyruğa alınır ve **bir sonraki 4h açılışında** (`t+1` kuralı) `CostModel` v1 açılış slippage'ıyla doldurulur. Bekleme sırasında stop kuralı geçerli kalır | Kapsam dışı |
| Drawdown halt (%15) | Yeni giriş yok; yeniden başlatma manuel | Kapsam içi (kernel) |
| Reconciliation tutarsızlığı | Yeni giriş yok; alarm; manuel inceleme | Kapsam dışı |

- Her eylem `trading.ops_actions`'a ve `audit.events`'e yazılır. Kohort kayıtlarını, manifestoları ve holdout tablolarını değiştirmez; `trading_app` ve `ops_api` rollerinin bu tablolarda hiçbir yetkisi yoktur (V9).
- Kohort değerlendirmesi, dondurulmuş tanımı arşiv verisi üzerinde bağımsız simüle ettiği için operasyonel eylemlerden etkilenmez (ADR-0012).

### 2.5 Reconciliation ve ledger invariant'ları (T)
- `trading.ledger_entries` append-only'dir. Her dolum; nakit hareketi, ücret ve slippage satırlarıyla, strateji ve kohort kimliği etiketiyle yazılır (ADR-0015).
- **Her işlenen mumda ve her açılışta:**
  - ledger'dan yeniden hesaplanan nakit ve pozisyonlar, durum anlık görüntüsüyle birebir;
  - sleeve başına nakit ≥ 0; pozisyon miktarları > 0;
  - portföy değeri = nakit + Σ miktar × son kapanış;
  - aynı `(sleeve, symbol, open_time, side)` için en fazla bir dolum (duplicate 0).
- Durum anlık görüntüsünün kanonik hash'i her mumda kaydedilir. Restart testi (§8) bu hash'i kullanır.

### 2.6 Strateji yükleme ve hash kontrolü (V14; T)
- Trading.Host yalnızca, `research.lifecycle_events`'teki son operasyonel durumu `PaperTrading` olan stratejileri çalıştırır.
- Tanım `research.strategy_versions`'tan okunur ve `StrategyHashGuard.LoadVerified(definition, frozen.definitionHash)` ile doğrulanır. Beklenen hash, Kohort 0 manifestosundan alınıp `research.frozen_strategies`'e yazılmış değerdir.
- **Uyuşmazlıkta** strateji çalıştırılmaz; `audit.events`'e `strategy_hash_mismatch` yazılır; `/health/ready` degraded olur; diğer stratejiler çalışmaya devam eder.

### 2.7 Korelasyon izleme (T12; bilgilendirme)
- Her 4h kapanışında, 20 paritenin son 30 günlük günlük log getirilerinden Pearson korelasyon matrisi hesaplanır. Kümeleme ve etkin bağımsız pozisyon sayısı `trading.correlation_snapshots`'a yazılır.
- Kümeleme: ortalama bağlantılı hiyerarşik kümeleme, mesafe `1 − ρ`, kesim `ρ* = 0,70`. Etkin N = `(Σ wᵢ)² / Σᵢ Σⱼ wᵢ wⱼ ρᵢⱼ`.
- Uyarı eşiği P1 analizinden sonra kullanıcı kararıyla belirlenir; o zamana kadar uyarı üretilmez. **Bağlayıcı değildir.**
- Bu metrikler `≥ C` fiyatlarından türediği için yalnızca kullanıcı Ops API'den görür.

## 3. Ops API (T; H23, H24)
- Trading.Host içinde, **yalnızca `127.0.0.1:55490`** üzerinden yayınlanır. Bearer token Docker secret dosyasındadır; kullanıcı kopyasını tutar. Token yoksa ya da yanlışsa 401.
- **Okuma uç noktaları:** `GET /status` (döngü, tazelik, kill switch, degraded nedenleri), `/positions`, `/pnl?by=strategy|cohort` (açık kohortlar dahil, D23), `/risk-config`, `/correlation`, `/lifecycle`.
- **Eylem uç noktaları:** `POST /kill-switch {on|off, reason}`, `/strategies/{name}/disable`, `/positions/close-all`, `/risk-config` (yeni sürüm). Hepsi audit'e yazılır.
- **`RiskConfig` değişikliği:** yalnızca `ops_api` DB rolü `trading.risk_config_versions`'a ekleme yapabilir; `trading_app`, `research_app` ve diğer roller yapamaz (H23). Gevşetici değişiklik (ör. tavan artışı) istekte açık bir `confirm` alanı ister. Yeni sürüm bir sonraki mum işlenişinde geçerli olur.
- **Manifesto, sonuç, doğrulama durumu ya da kohort kaydı için uç nokta yoktur.** Route tablosu testi bunu sabitler (H24).
- Ops API ve trading döngüsü aynı process'tedir, ama ayrı DB kimlikleri kullanır (`ops_api` ve `trading_app`).

## 4. Veritabanları, roller ve ağlar

### 4.1 Veritabanları (U-M5-2, A-5)
| Veritabanı | İçerik | Not |
|---|---|---|
| `ops` | `portfolio_dev`'in yeniden adlandırılmış hali + M5 migration'ları (`market.live_candles`, `trading.*`, `research.*` view'ları) | Yeniden adlandırmadan önce şifreli yedek (§7.4) |
| `holdout` | Evaluator şeması (`0001` + `0002`); M5'te pencere verisi yok | Yalnızca `eval_*` rolleri |
| `holdout_henv` | H19 için geçici scratch veritabanı | H-ENV sonunda silinir |
| `holdout_synth` | M3 sentetik kayıtları | Silinmez; `CONNECT` herkesten alınır |

### 4.2 Login rolleri (T)
| Rol | Veritabanı | Kimlik bilgisi nerede | Bağlanabildiği adres (`pg_hba`) |
|---|---|---|---|
| `ops_owner` | `ops` (owner, migration, yedek) | `secrets/` (yalnızca kullanıcı script'leri) | loopback yayınının geçidi ve `ops` ağı |
| `research_app` | `ops` | `.env` (Claude Code'un araçları ve Research.Host) | loopback ve `ops` ağı |
| `trading_app` | `ops` | `secrets/` → Trading.Host | `ops` ağında Trading.Host IP'si |
| `ops_api` | `ops` | `secrets/` → Trading.Host | `ops` ağında Trading.Host IP'si |
| `market_import` | `ops` | `secrets/` (kullanıcının import koşuları) | loopback |
| `status_relay` | `ops` | `src/HoldoutEvaluator/secrets/` → evaluator | yalnızca `holdout` ağında evaluator IP'si |
| `eval_owner` | `holdout`, `holdout_henv` (owner, migration, yönetim komutları) | `src/HoldoutEvaluator/secrets/` | yalnızca evaluator IP'si |
| `eval_registry`, `eval_diagnostic`, `eval_prospective` | `holdout`, `holdout_henv` | `src/HoldoutEvaluator/secrets/` | yalnızca evaluator IP'si |
| superuser | — | `secrets/` | **yalnızca container içi yerel soket**; tüm `host` bağlantıları `reject` |

- Parolalar migration'larda bulunmaz. `m5-bootstrap.ps1` (kullanıcı) rastgele üretir, secret dosyalarına yazar ve `ALTER ROLE … LOGIN PASSWORD` uygular. Yöntem `scram-sha-256`'dır.
- `REVOKE CONNECT ON DATABASE ops, holdout, holdout_henv, holdout_synth FROM PUBLIC`; `GRANT CONNECT` yalnızca tablodaki rollere.
- `research_app`'in `market` şemasındaki geniş `SELECT` yetkisi kaldırılır. Yalnızca `market.candles`, `market.dataset_snapshots` ve `market.source_files`'a açıkça verilir. `market.live_candles`'a ve `trading.*`'a yetkisi yoktur; default privileges ile de verilmez.

### 4.3 Docker ağları ve statik IP'ler (ADR-0013 rev. notu; T)
| Ağ | Alt ağ | `internal` | Üyeler (IP) |
|---|---|---|---|
| `ops` | 172.30.1.0/24 | hayır | postgres .10, trading-host .20, research-host .30 |
| `eval` | 172.30.2.0/24 | evet | holdout-evaluator .20, research-host .30 |
| `holdout` | 172.30.3.0/24 | evet | postgres .10, holdout-evaluator .20 |
| `egress` | 172.30.4.0/24 | hayır | holdout-evaluator .20 |

- PostgreSQL host'a yalnızca `127.0.0.1:55432` olarak yayınlanır. Evaluator ve Research.Host'un portları yayınlanmaz; Ops API yalnızca `127.0.0.1:55490`.
- **`pg_hba.conf` (sıra önemli; ilk eşleşme geçerli):**
  1. `local all <superuser> scram-sha-256` (yalnızca soket);
  2. `host holdout,holdout_henv eval_owner,eval_registry,eval_diagnostic,eval_prospective 172.30.3.20/32 scram-sha-256`;
  3. `host ops status_relay 172.30.3.20/32 scram-sha-256`;
  4. `host ops trading_app,ops_api 172.30.1.20/32 scram-sha-256`;
  5. `host ops research_app 172.30.1.30/32 scram-sha-256`;
  6. `host ops ops_owner,research_app,market_import <loopback yayın kaynağı> scram-sha-256`;
  7. `host all all 0.0.0.0/0 reject`.
- **Doğrulanmamış varsayım:** Docker Desktop'ta loopback'e yayınlanmış porttan gelen bağlantının PostgreSQL'e hangi kaynak adresle ulaştığı. Kapı C'de bootstrap bu adresi ölçer ve 6. satıra yazar. Holdout satırları evaluator'ın statik IP'sine bağlı olduğu için bu varsayım holdout korumasını etkilemez. H5 bunu ayrıca sınar.
- `postgresql.conf`: `listen_addresses='*'` (yalnızca container ağları), `shared_buffers=256MB`, `max_connections=40`, `log_connections=on`, `password_encryption=scram-sha-256`.

### 4.4 Kaynak sınırları (ADR-0013 §3; TBD ölçümle)
PostgreSQL 1 GB / 1.0, Trading.Host 384 MB / 0.5, Research.Host 1.5 GB / 2.0 (iş bazlı), HoldoutEvaluator 1.5 GB / 2.0 (iş bazlı), H-ENV probe 256 MB / 0.5 (tek seferlik). Kapı C'de `docker stats` ile ölçülür; değişiklik ADR-0013'e not olarak yazılır (T).

### 4.5 Lifecycle ve dondurulmuş strateji kayıtları (ops; T)
- `research.frozen_strategies` (append-only): kohort referansı, üye sırası, ad, `definitionHash`, manifesto hash'i, pariteler ve kategorileri, aralık, `RiskConfig` snapshot'ı. Kohort 0 için `docs/cohorts/cohort-0.json`'dan bir kez yüklenir (manifesto hash'i doğrulanır, dosya değişmez).
- `research.lifecycle_events` (append-only): `Candidate → Frozen → PaperTrading → TestnetActive → Disabled | Retired | Rejected` geçişleri, kanıt referansı (parity raporu hash'i) ve aktör. `research_app` yalnızca `Frozen → PaperTrading` dahil araştırma tarafı geçişlerini yazar. `Disabled` geçişini Ops API yazar. `trading_app` yalnızca okur.
- Doğrulama durumu bu tablolarda **yoktur**; yalnızca `results.prospective_results`'tan türetilir (ADR-0012).

## 5. Parity replay (`Frozen → PaperTrading`; ADR-0015, T22)

### 5.1 Tanım
- **Replay girdisi:** Kohort 0'ın iki stratejisi, 20 çekirdek parite, 4h, `R = [2022-01-01, C)`, `RankingWindow` başından ısınma, `DatasetSnapshot a6c4e89b…`, sermaye ve sleeve'ler §2.2'deki gibi, `RiskConfig` v2 = manifesto risk bloğu + `ExperimentalPairsMaxExposure 0,10` + `MaxDataLagBars 2`.
- **İki yol:**
  - **(a) Referans:** kernel `PortfolioEngine` batch koşusu (çok sleeve'li);
  - **(b) Trading:** Trading modülünün gerçek döngü kodu (`PaperCycle`). Mumlar replay kaynağından saatlik döngü takvimiyle beslenir; ledger, durum ve olaylar Testcontainers PostgreSQL'de, M5 migration'larıyla tutulur.
- **L1 olayları:** `(time, sleeve, symbol, kind ∈ {EntrySignal, ExitSignal, EntryFill, ExitFill, StopSet, StopRaised, StopTrigger}, price/level, reason)` ve her kapanışta risk öncesi birim hedef ağırlıklar (12 anlamlı basamak).
- **L2 olayları:** `(time, sleeve, symbol, decision ∈ {Approved, Clipped, Rejected}, reason, notional_before, notional_after)` ve sizer çıktısı.
- **Kanonik biçim:** satır başına bir olay, alanlar `|` ile ayrılır, invariant culture; zaman ISO "O"; decimal'ler normalize; sıralama zaman, sleeve, rank, tür. Hash = SHA-256.
- **Kabul:** (a) ve (b)'nin L1 akışları ve L2 akışları **bayt bayt eşit.** İlk farkta replay durur ve fark satırı raporlanır. Başarıda `research.lifecycle_events`'e her strateji için `Frozen → PaperTrading` yazılır (kanıt: rapor hash'i).
- **Regresyon kilidi (ön koşul):** tek sleeve ve 10.000 USDT ile kernel motoru, M2'nin dört sonuç hash'ini birebir üretir (`parity-m3`).

### 5.2 Restart ve tazelik varyantları (Kapı B testleri; sentetik veri)
- Replay sırasında rastgele noktalarda (seed'li) process durdurulup yeniden başlatılır. Son durum hash'i ve L1/L2 akışları kesintisiz koşuyla aynıdır.
- Bir sembolün mumları `k > 2` mum geciktirilir. Gecikme süresince o sembole giriş yoktur, çıkışlar ve stop'lar çalışır. Veri gelince mumlar sırayla işlenir.

### 5.3 L3 fark raporu (bilgilendirici; doğrulamaya girmez)
- **Paper ↔ kernel:** sleeve başına işlem zamanlaması eşleşme oranı, işlem başına yüzde getiri, maruziyete göre normalize getiri, slippage (bps). Mutlak tutarlar karşılaştırılmaz.
- **Kategoriler (ADR-0015):** ortak sermaye ve portföy kısıtları; `MIN_NOTIONAL` atlamaları; dolum ve slippage; canlı/arşiv mum farkları; operasyonel eylemler (kill switch, devre dışı, manuel kapatma); kesintiler.
- M5'te L3 iki yerde üretilir:
  - parity replay'de, izole tek sleeve kernel koşusu ile çok sleeve'li koşu arasında (`R` verisi, Claude görebilir);
  - paper çalışmasında, kullanıcının raporu olarak. `≥ C` fiyatlarından türediği için Claude görmez (U-M5-5).

## 6. Holdout ortamı ve H-ENV

### 6.1 Prosedür (kullanıcının etkileşimli koşusu)
1. `m5-deploy.ps1`: image'lar derlenir; evaluator image digest'i `docker image inspect` ile alınır; `eval_owner` ile `register-release <digest> <engineSha>` çalıştırılır (`results.evaluator_releases`).
2. `m5-henv.ps1`:
   - `holdout_henv` scratch DB'sini kurar (soket üzerinden superuser, kullanıcı oturumu);
   - probe container'larını §6.2'deki ağ konumlarından tek seferlik çalıştırır;
   - host tarafı kontrollerini yapar;
   - scratch DB'yi siler.
3. **Rapor** yalnızca `(test id, PASS|FAIL, kısa neden kodu)` satırlarından oluşur; veri, satır ve parola içermez. Rapor hash'lenir ve repo dışında saklanır. Kullanıcı raporu (ya da yalnızca özetini) Claude Code ile paylaşabilir.
4. Tümü PASS ise kullanıcı `henv-mark-verified --digest <d> --report-hash <h>` komutunu çalıştırır. Komut etkileşimli onay ister ve yönlendirilmiş girdiyi reddeder. Bayrak `results.environment_flags`'a yazılır ve audit'e girer.
5. FAIL varsa durulur; düzeltme Kapı B kuralıyla yapılır; H-ENV yeniden koşulur.

### 6.2 Testler
| # | Konum | Kontrol | Beklenen |
|---|---|---|---|
| H5 | Host | `127.0.0.1:55432` üzerinden `holdout` ve `holdout_henv` DB'lerine `research_app`, `eval_*` ve superuser ile bağlantı; evaluator için yayınlanmış port var mı (`docker compose port`, TCP 55480) | `pg_hba` reddi; port yok |
| H6 | `ops`+`eval` (Research.Host konumu) | `holdout` DB'sine bağlantı (`research_app` ve `eval_*` adlarıyla) | Ağ ya da `pg_hba` reddi |
| H6b | Host, `research_app` | `market.live_candles`, `trading.*` okuma; `research.revealed_cohorts` ve `revealed_paper_summary`'de açığa çıkmamış kohort | Yetki hatası ya da 0 satır |
| H14 | `ops` (Trading.Host konumu) | `holdout-evaluator:8080` HTTP | Ağ düzeyinde başarısız |
| H19 | Evaluator, `holdout_henv` | H9, H10, H15, H16, V4 ve V5 senaryoları, gerçek evaluator image'ında ve gerçek login'lerle (`henv-selftest`) | M3 testleriyle aynı sonuçlar |
| H20 | Evaluator | Çalışan image digest'i = `results.evaluator_releases` kaydı | Eşleşir |
| H21 | Evaluator ve `ops` konumu | `eval_*` ile `ops` DB'ye bağlantı; `status_relay` ile herhangi bir tabloyu okuma ve `cohort_status` dışına yazma; `ops` ağından `status_relay` ile bağlantı | Başarısız |
| H22 | Evaluator, `holdout_henv` (ve seed'den sonra `holdout`) | Penceresi kapanmamış kohort için `prospective.candles` / `prospective_runs` / `prospective_results`'a ekleme | Trigger reddeder; hiçbir satır yazılmaz |
| H23 | Host | `trading_app` ve `research_app` ile `trading.risk_config_versions`'a ekleme; tokensız Ops API `POST /risk-config` | Reddedilir |
| H24 | Host | Ops API: `GET /pnl?by=cohort` açık kohortu içerir; manifesto, sonuç ve doğrulama yazan yol yok (route listesi ve 404) | Doğru |
| H25 | `ops` (Research.Host konumu) | `research_app` ile `research.cohort_status`'a ekleme; Research.Host container'ında `status_relay` kimlik bilgisi (env adları, secret dosyaları, config) | Yetki hatası; bulunmaz |
| H26 | Host, `research_app` | `pg_database.datacl`: `ops`, `holdout`, `holdout_henv` ve `holdout_synth`'te `PUBLIC` için `c` yok; yalnızca §4.2'deki roller | Doğru |
| H27 | Host | Compose: her serviste `mem_limit` ve `cpus`; holdout verisi yalnızca volume'de (repo dizininde holdout dosyası ya da dump yok) | Doğru |
| H28 | Host | `.claude/settings.json` deny kuralları (§11) | Doğru |

- **Pozitif kontroller:** her negatif testin yanında aynı konumdan izinli bağlantının çalıştığı gösterilir (ör. evaluator IP'sinden `eval_registry` `holdout`'a bağlanır). Böylece "her şey zaten kapalı" durumu PASS sayılmaz.
- **Bu testlerin güvencesi sınırlıdır:** aynı makinedeki yönetici ya da Docker yetkisine karşı bir şey göstermezler (ADR-0013 artık riski).

### 6.3 Kohort 0'ın gerçek holdout DB'ye aktarılması (V13 istisnası; A-2)
- Kullanıcı `seed-cohort0` komutunu `eval_owner` ile çalıştırır. Girdi, commit'lenmiş ve değişmemiş `docs/cohorts/cohort-0.json`'dır; `registered_at` = `be2bce3` commit zamanı **2026-10-15T22:52:33Z** (git'ten okunur). `A_0` = **2026-10-17T00:00Z** ve pencere sonu 2027-10-17T00:00Z veritabanında türetilir ve değişmez.
- **Engine bağı (A-2):** manifestoda `imageDigest = local-build:…` ve `engineSha = de22051a…` var. Bu release, seed release'i olarak `results.evaluator_releases`'e eklenir. Kohort 0'ı ileride değerlendirecek gerçek image (`D1`) için `results.release_equivalences`'a append-only bir kayıt yazılır:
  - `(from local-build, to D1, kanıt)`;
  - kanıt: `D1`'in engine'i M2'nin dört sonuç hash'ini ve Kohort 0'ın iki `policyResultHash`'ini birebir üretir (`parity-m3` çıktısının hash'i).
  - `run_rules` yalnızca bu kayıt varsa Kohort 0'ın `D1` ile koşulmasına izin verir.
  - Kayıt audit'e yazılır. Manifesto değişmez.
- **Sınırlama:** eşdeğerlik regresyon testiyle gösterilir, kanıtlanmaz. Kohort 0'ın sonucu raporlarda bu bağı belirtir.
- `status_relay`, `research.cohort_status`'a `sealed` yazar. Claude Code bunu `research_app` ile okuyarak doğrular (yalnızca durum).
- Seed'den sonra H22, gerçek `holdout` DB'de Kohort 0 penceresi için tekrarlanır (hiçbir satır yazılmaz).

## 7. Kapı C koşuları

### 7.1 Paper çalışması (≥ 7 gün; kullanıcı başlatır)
- Kullanıcı `m5-paper-start.ps1` ile Trading.Host'u başlatır; canlı REST erişimi bu adımda kullanıcı onayıyla açılır (A-3).
- Süre boyunca:
  - makinenin kapalı ve uykuda kaldığı aralıklar kaydedilir (garanti yok, ADR-0013);
  - en az bir planlı restart yapılır;
  - en az bir ağır Research.Host işi (`calibrate --quick`, sentetik) koşulur ve döngü süreleri ölçülür;
  - kill switch bir kez açılıp kapatılır ve audit kontrol edilir.
- "Tümünü kapat" gerçek koşuda denenmez; Kapı B testleriyle doğrulanır. Paper pozisyonları operasyonel olarak kapatılabilir; bu kullanıcı kararıdır.

### 7.2 Döngü kabulü (T)
- Makine açıkken her saat için `trading.cycle_metrics`'te bir satır vardır (kaçırılan döngü 0).
- Her 4h mumu, kapanışından en geç 5 dakika sonra işlenmiş olur. Ağır iş sırasında da bu sağlanır.
- Kesintiden sonra kaçırılan mumlar sırayla işlenir; reconciliation tutarlıdır.

### 7.3 Claude Code'a görünen rapor (U-M5-5)
`m5-paper-report.ps1` (kullanıcı koşar) yalnızca şunları üretir:
- döngü sayısı; döngü süresinin medyanı, p95 ve maksimumu; gecikmeli döngü sayısı;
- kaçırılan ve sonradan işlenen 4h mum sayıları; kesinti aralıkları (başlangıç, bitiş);
- reconciliation koşu ve tutarsızlık sayıları; invariant ihlali sayısı; duplicate dolum sayısı;
- hata ve uyarı sayıları (sınıf bazında); 429 ve 418 sayıları; degraded nedenleri;
- operasyonel eylem sayıları (tür bazında);
- servis başına bellek ve CPU özeti.

**Fiyat, PnL, pozisyon, işlem, sinyal ve korelasyon içermez.**

### 7.4 Yedek ve geri yükleme (U-M5-3; T)
- `m5-backup.ps1` (kullanıcı):
  - `ops_owner` ile, `ops` ağındaki tek seferlik bir `postgres:17-alpine` container'ında `pg_dump -Fc` alır;
  - `BackupCli` dökümü AES-256-GCM ile şifreler (anahtar `secrets/` altında, 32 bayt);
  - dosyayı yapılandırılmış hedef klasöre yazar. **Hedef mutlak yol olmalıdır; repo dizini ve `OneDrive` altı reddedilir.**
- Holdout değerlendirme kayıtları (`results.*` manifesto, üye, release, bayrak, koşu ve sonuç tabloları; **mum tabloları hariç**) evaluator'ın `export-records` komutuyla ayrıca dışa aktarılır ve aynı şekilde şifrelenir.
- `m5-restore-test.ps1`: dökümü `ops_restore_test` DB'sine geri yükler; tablo başına satır sayısı ve kanonik içerik hash'ini canlı `ops` ile karşılaştırır (çıktı yalnızca tablo adı, sayı, hash eşleşmesi); sonra scratch DB'yi siler.
- Günlük çalıştırma, Windows Görev Zamanlayıcı ile kullanıcı tarafından ayarlanabilir (isteğe bağlı). M5 kabulü için bir yedek, harici diske kopya ve bir geri yükleme denemesi yeterlidir.

## 8. Kapı B test listesi (bağlayıcı)
- **Regresyon:** mevcut testlerin tamamı; M2'nin dört hash'i (`parity-m3`, gerçek `R`); DSL ↔ C# parity.
- **Risk tablosu** (risk-model §2 her satır; testing §7.3):
  - 5 pozisyon (portföy geneli, sleeve sırası); varlık %20 ve toplam %80 (sleeve toplamı);
  - günlük kayıp %3 → 24 saat giriş yok; drawdown %15 halt ve manuel yeniden başlatma;
  - tazelik > 2 mum → giriş yok, çıkış serbest;
  - efektif stop (D25) ve stop kuralı (gap, `Low ≤ S`, giriş mumu, aynı mumda stop önceliği, seviyenin mum içinde değişmemesi);
  - `MIN_NOTIONAL` atlaması;
  - deneysel tavan: tam sınırda kabul; aşan alımın kırpılması ve kırpılmış tutar `MIN_NOTIONAL`'ın altındaysa ret; fiyat hareketiyle aşımda zorunlu satış yok ve yeni deneysel giriş yok; çıkışlar serbest; diğer limitlerin değişmemesi; yetkisiz değişikliğin reddi (DB rolü ve API).
- **Paper:** stop kuralı ve dolumlar kernel ile birebir; restart (§5.2); bayat veri (§5.2); duplicate dolum 0; ledger invariant'ları (property testi).
- **Operasyonel:**
  - kill switch girişleri durdurur ve bekleyen girişleri iptal eder, stop kuralı sürer;
  - devre dışı bırakma; tümünü kapat (`t+1` açılış);
  - her eylem audit'te; audit ve ledger'da `UPDATE`/`DELETE` reddi;
  - V9 (operasyonel eylemler kohort, manifesto ve holdout tablolarına dokunmaz; ilgili rollerin yetkisi yok);
  - V14 (hash uyuşmazlığında strateji çalışmaz, alarm, diğerleri sürer).
- **Canlı istemci:** host/yol allowlist'i, redirect reddi, yalnızca kapanmış ve hizalı mum, OHLC, sonradan değişen mumun anomali kaydı, 429/`Retry-After`, 418 → halt; testlerin ağa çıkmadığını gösteren test.
- **Roller** (Testcontainers, rol başına gerçek login): H6b, H21 (DB kısmı), H23 (DB kısmı), H25 (DB kısmı) ve H26 analogları; `research_app`'in `live_candles`'ı default privileges ile de görememesi.
- **Yapılandırma:**
  - `pg_hba.conf` kural testi (dosya ayrıştırılır; holdout satırları yalnızca evaluator IP'si ve `eval_*`; superuser için `host` satırı yok; son satır `reject`);
  - compose testi (H27; evaluator portu yayınlanmamış; ağ üyelikleri ve `internal` bayrakları; statik IP'ler `pg_hba` ile tutarlı);
  - settings deny kuralları (H28 ve §11).
- **Ops API:** token zorunlu; route tablosu (H24); `RiskConfig` sürümleme ve audit.
- **Mimari:** Execution, Risk ve Trading.Host; Research'e, Researcher'a, Ai'ya ve HoldoutEvaluator'a referans vermez. Kernel'e eklenen manifesto okuyucu holdout'a bağımlı değildir. Trading'de holdout sır adları yoktur.
- **Evaluator** (§10): arşiv kaynağı (sahte handler; yalnızca kayıtlı pencere; checksum; allowlist; redirect reddi; erken import'un uygulama katmanında reddi), gerçek login'lerle rol testleri, digest'e bağlı ortam bayrağı, release eşdeğerliği kuralı, seed'in gerçek DB modunda tekrarlanabilirliği (synthetic DB'de), kayıt dışa aktarımında mum tablosu olmaması.

## 9. P1 — korelasyon kümesi maruziyet tavanı için tarihsel etki analizi (ön kayıt; A-4)
- **Amaç:** bağlayıcı bir küme tavanının (P1) tarihsel etkisini ölçmek ve bir değer önermek. Karar kullanıcıdadır (risk limiti; requirements §7).
- **Veri:** yalnızca `R`; 20 çekirdek parite, 4h; volatilite ile aynı gün sonu kapanışları.
- **Korelasyon:** her gün sonunda, yalnızca o güne kadarki verilerle (look-ahead yok), son 30 günlük günlük log getirilerinin Pearson matrisi. Eksik veri olan parite o gün kümelemeye girmez (tek başına küme).
- **Kümeleme:** ortalama bağlantılı hiyerarşik, mesafe `1 − ρ`. Birincil kesim `ρ* = 0,70`; duyarlılık için 0,60 ve 0,80 (yalnızca rapor).
- **Tavan:** küme başına toplam maruziyet ≤ `c` × portföy değeri. Deneysel tavanla aynı kural: aşan alım kırpılır; fiyat hareketiyle aşımda zorunlu satış yok; çıkışlar serbest.
- **Izgara:** `c ∈ {0,30; 0,40; 0,50; 0,60; tavan yok}`.
- **Koşular:** Kohort 0'ın iki stratejisi, `policy` koşusu, tek sleeve, 10.000 USDT, maliyet v1. Birincil: 2 × 5 = 10 koşu. Duyarlılık: 2 × 2 × 5 = 20 koşu.
- **Metrikler:** M_R (m3 §4), maksimum drawdown, en kötü 30 günlük getiri, işlem sayısı, kırpılan ve reddedilen giriş sayısı, ortalama maruziyet, ortalama etkin N. Tavansız koşuda ayrıca en büyük küme payının dağılımı (medyan, p95, maksimum).
- **Öneri kuralı (sonuç görülmeden; birincil kesim):**
  1. Her `c` için iki stratejide de `ΔDD = DD(tavansız) − DD(c)` ve `ΔM = M_R(c) − M_R(tavansız)` hesaplanır.
  2. Uygun `c`: en az bir girişi kırpar ya da reddeder, **ve** iki stratejide de `ΔM ≥ −1,0` puan (yıllık), **ve** en az bir stratejide `ΔDD ≥ 0,5` puan.
  3. Uygun `c` varsa önerilen değer, uygunlar içindeki **en büyük** `c`'dir (en az kısıtlayıcı). Yoksa öneri **"bağlayıcı tavan yok, yalnızca izleme"**dir.
  4. **Uyarı eşiği önerisi:** tavan öneriliyorsa `0,9 × c`; önerilmiyorsa tavansız koşudaki en büyük küme payının p95'i, iki strateji arasındaki büyük değer.
- **Deneme muhasebesi (T):** strateji tanımları değişmez ve sonuç hiçbir strateji kapısına ya da kohorta girmez; analiz bir risk katmanının etkisini ölçer. Bu nedenle `TrialFamily` sayacına **girmez.** Gerekçe ve koşu sayısı CHANGELOG'a yazılır.
- **Sınırlamalar:** iki baseline'ın `R`'deki M_R'si negatif; 20 paritenin yüksek ortak BTC faktörü kümeleri büyütür; sonuç ileriye genellenemez.
- **Çıktı:** `docs/m5-p1-analysis.md` (birincil tablo, duyarlılık, öneri, sınırlamalar).

## 10. Evaluator ve `*Holdout*` dosya değişiklik listesi (U-M5-4; A-6)
Bu liste dışında evaluator ya da adında `Holdout` geçen dosya değiştirilmez. `HoldoutSecretReferenceTests.cs` değişmez.

| # | Dosya | Değişiklik | Gerekçe |
|---|---|---|---|
| E1 | `src/HoldoutEvaluator/Program.cs` | `CreateDatabase` yolunun ve sabit `SyntheticWindowSource`'un kaldırılması. Pencere kaynağı yapılandırmadan seçilir; sentetik kaynak yalnızca adı `holdout_synth` ya da `holdout_henv` olan veritabanında kabul edilir. Rol başına connection string. Yönetim komutları: `register-release`, `henv-selftest`, `henv-mark-verified`, `seed-cohort0` (gerçek DB modu), `export-records`, `release-check`. Çalışan image digest'i yapılandırmadan | Gerçek ortam, H19–H20, §6.3, §7.4 |
| E2 | `src/HoldoutEvaluator/Services.cs` | `HoldoutDatabase`: `SET ROLE` emülasyonu yerine rol başına gerçek login. Testler için `SET ROLE` uygulaması ayrı bir sınıf olarak kalır. `ProspectiveService`'te release eşdeğerliği kontrolü | ADR-0013 §2, §6.3 |
| E3 | `src/HoldoutEvaluator/ArchiveWindowSource.cs` (yeni) | `IWindowSource` arşiv uygulaması: yalnızca `data.binance.vision` dosya yolları; redirect reddi; checksum zorunlu; yalnızca kayıtlı kohort penceresi ve ısınma; uygulama katmanında erken import reddi. MarketData modülüne bağımlı değildir (mimari kural); Kernel'deki CSV ayrıştırıcı ve dosya planlayıcıyı kullanır | architecture §6.2 "evaluator'ın veri edinimi" |
| E4 | `src/HoldoutEvaluator/CohortSeeder.cs` | Gerçek DB modu: seed release'i ve `release_equivalences` kaydı (§6.3); tekrar çalıştırmanın reddi | A-2 |
| E5 | `src/HoldoutEvaluator/Persistence/Migrations/0002_m5_environment.sql` (yeni) | `environment_flags`'a `image_digest` ve `report_hash`; `environment_verified(digest)`; `Validated` kapısının koşunun digest'ine bağlanması; `release_equivalences` tablosu (append-only) ve `run_rules`'a eşdeğerlik koşulu; `eval_owner` için yönetim fonksiyonları. `0001` değişmez | ADR-0013 §4 ("evaluator kodundaki her değişiklik bayrağı sıfırlar"), §6.3 |
| E6 | `src/HoldoutEvaluator/evaluator-up.ps1`, `evaluator-seed-cohort0.ps1` | Kaldırılır; yerlerini `scripts/m5-*.ps1` ve E7 alır. M3 sentetik profili compose'dan çıkar; sentetik testler Testcontainers'da kalır | Port yayını (H5), tek yol |
| E7 | `src/HoldoutEvaluator/compose.holdout.yml` (yeni), `src/HoldoutEvaluator/secrets/` (git-ignored) | Evaluator servisi, ağları, statik IP'si, secret bağlamaları; ana compose bu dosyayı `include` eder. Holdout sır adları yalnızca bu dizinde | ADR-0013 §4, `HoldoutSecretReferenceTests` |
| E8 | `src/HoldoutEvaluator/EnvironmentProbe/` (yeni proje) | H-ENV probe'u: bağlantı denemeleri, pozitif kontroller, PASS/FAIL raporu; veri okumaz | §6 |
| E9 | `src/HoldoutEvaluator/Dockerfile` | Probe ve yönetim komutlarının aynı image'da bulunması; çalışma zamanı değişmez | H20 (tek digest) |
| E10 | `tests/HoldoutEvaluator.Tests/**` | E1–E5 ve E8'in testleri; mevcut testler korunur | Kapı B |

`Api.cs`, `Evaluation.cs` (karar kuralları) ve `Manifest.cs` değişmez. Research.Host'un evaluator'a kohort gönderme istemcisi M5'te eklenmez; yeni bir kohort oluştuğunda eklenir (YAGNI).

## 11. Korkuluklar (T17 gözden geçirmesi; Kapı A'da uygulanır)
- **`.claude/settings.json` yeni deny kuralları:**
  - `WebFetch(domain:data-api.binance.vision)`, `WebFetch(domain:api.binance.com)` ve bu host'lar için `curl`, `wget` ve PowerShell kalıpları;
  - kullanıcıya ait script'ler: `*m5-bootstrap*`, `*m5-deploy*`, `*m5-henv*`, `*m5-paper-start*`, `*m5-backup*`, `*m5-restore-test*`, `*m5-paper-report*`;
  - evaluator ve probe çalıştırma: `docker compose run*holdout*`, `docker compose run*henv*`, `docker run*holdout*`;
  - holdout'a doğrudan istemci: `*psql*holdout*`.
- **`CLAUDE.md`:**
  - `ops`'a yalnızca `research_app` ile bağlanılır; `market.live_candles` ve `trading.*` okunmaz;
  - M5 script'leri kullanıcı tarafından çalıştırılır;
  - paper raporlarında yalnızca operasyonel alanlar;
  - `dev-up.ps1` Kapı C'den sonra yalnızca `research_app` ile çalışır.
- Bu kurallar teknik güven sınırı değildir (ADR-0013 §6). Testleri Kapı B'de yazılır (`RepositoryRuleTests`).

## 12. Asgari paper süresi (`PaperTrading → TestnetActive`, TBD; T, kullanıcı onayıyla)
- **Yöntem:** M5 paper çalışmasında kritik hata sayısı, kesinti oranı ve 4h mum başına operasyonel olay sıklığı ölçülür. Öneri, en az **14 gün** ve en az **60 işlenmiş 4h mum** ile, son 7 günde sıfır kritik hata (reconciliation tutarsızlığı, invariant ihlali, duplicate, hash uyuşmazlığı, 418) şartıdır.
- Değer, M5 kabul raporunda ölçümle birlikte önerilir ve kullanıcı onayıyla `requirements.md` §8'den çıkar.

## 13. Kabul (roadmap M5'e eşleme)
| Roadmap kriteri | Kanıt |
|---|---|
| H-ENV gerçek ortamda geçti; bayrak kullanıcı oturumuyla set edildi ve audit'te | §6 raporu ve hash'i; `environment_flags` satırı |
| Research canlı mum ve açığa çıkmamış paper okuyamıyor; `cohort_status`'a yazamıyor; `eval_*` `ops`'a bağlanamıyor; erken import reddi | H6b, H21, H22, H25 |
| `status_relay` kimliği yalnızca evaluator'da | H25, H21 |
| Bellek sınırları; ağır iş sırasında döngü kaçırılmıyor | §4.4, §7.2 |
| Deneysel parite tavanı risk tablosu testleri | §8 |
| Operasyonel güvenlik eylemleri, audit, kohort kayıtlarının değişmemesi | §8, §7.1 |
| Parity replay L1/L2; L3 normalize; risk tablosu; stop kuralı; bayat veri; restart | §5, §8 |
| 7 gün paper; kesintiler raporlandı; reconciliation; yedekten geri yükleme | §7 |
| P1 raporu sunuldu | §9 |
