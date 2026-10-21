# ADR-0020: Yerel web dashboard (M9a) ve sinyal radarı

- Durum: **Kabul edildi (Kapı A, kullanıcı onayı 2026-10-21)** · Tarih: 2026-10-21
- Karar türü: **U** (M9'un bir kısmının öne çekilmesi; D32) ve **T** (sunum, güvenlik modeli, uç noktalar, radar semantiği).
- Uygulama durumu: **Uygulanmadı.**
- İlgili: ADR-0013, ADR-0015, ADR-0018; `architecture.md` §10, §11; `m5-protocol.md` §3 (H24); `roadmap.md` M9; D23, D25, D29, D32

## Bağlam
- Kullanıcı paper durumunu PowerShell çıktısı yerine tarayıcıdan izlemek istiyor (U, 2026-10-21). `requirements.md` §4: "Dashboard M9'da; öncesinde API ve minimal bir durum sayfası."
- Ops API (`127.0.0.1:55490`, bearer token) yalnızca JSON döndürür. Route tablosu H24 testiyle sabittir: her route `OpsApi.Routes`'ta olmalı ve route metni `cohort`, `manifest`, `result`, `validat` içeremez.
- Paper modunda ayrı `Order`/`OrderIntent`/`Signal` tabloları yoktur; sinyaller (L1) ve risk kararları (L2) `trading.engine_events` satırlarıdır (ADR-0018).
- `ops_api` rolü `ai.*`, `audit.events`, `research.strategy_versions` ve deney kayıtlarını okuyamaz.
- Claude Code canlı mumları, `trading.*` verisini ve PnL'i okumaz (CLAUDE.md). Dashboard bu verileri kullanıcıya gösterir.

## Karar

### 1. Kapsam: M9a (U, D32)
M9'un üç kabul kriteri (zincir, DecisionTrace/degraded/LLM acil durdurma, görev sınıfı bazında maliyet) bugünkü veriyle karşılanır. Testnet emirleri (M6), web bulguları (M7) ve allocator önerileri (M8) eklendikçe aynı ekrana bağlanır; M9'un tam kabulü roadmap'te yerinde kalır.

### 2. Sunum
- Trading.Host içinde, gömülü kaynak olarak üç dosya: `dashboard.html`, `app.js`, `app.css`. Vanilla JS; build, npm, CDN ve harici font yok. Route'lar `GET /dashboard`, `GET /dashboard/app.js`, `GET /dashboard/app.css`. Bu üç route veri taşımaz ve token istemez.
- Veri, token'lı salt-okunur JSON uç noktalarından `fetch` ile gelir. Yeni POST uç noktası eklenmez; operasyonel eylemler mevcut uç noktalarda kalır.

### 3. Güvenlik modeli
- **Token:** sayfa token'ı bir giriş kutusundan alır, yalnızca o sekmenin `sessionStorage`'ında tutar ve yalnızca `Authorization: Bearer` başlığıyla gönderir. URL'de, cookie'de ya da sunucu tarafı oturumda token yoktur; bu yüzden CSRF yüzeyi oluşmaz.
- **Başlıklar:** `Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`. Inline script yok.
- **DNS rebinding:** `Host` başlığı yapılandırılmış loopback adlarından biri değilse (`127.0.0.1:55490`, `localhost:55490`) istek 421 ile reddedilir.
- **Token karşılaştırması:** iki tarafın SHA-256 özeti `FixedTimeEquals` ile karşılaştırılır (uzunluk sızıntısı kapanır).
- **Gösterilmeyenler:** secret'lar, connection string'ler, prompt metinleri, holdout DB verisi, evaluator sonuçları, manifestoların ham metni dışındaki holdout kayıtları. `HoldoutEnvironmentVerified` yalnızca durum ve zaman olarak gösterilir.
- Port yalnızca loopback'e yayınlanır (değişmez).

### 4. Uç noktalar (GET, token'lı)
| Route | İçerik |
|---|---|
| `/overview` | Tüm run'ların toplamı ve run başına: nakit, equity, gerçekleşmiş/gerçekleşmemiş PnL, drawdown, toplam maruziyet; deneysel parite maruziyeti ve `experimentalPairsMaxExposure`; son korelasyon kümeleri ve D29 tavanı/uyarısı |
| `/sleeves` | Kohort × strateji: lifecycle durumu, sermaye, equity, PnL, işlem sayısı, açık pozisyonlar (giriş, son kapanış, efektif stop; D25 hard stop ile strateji stop'undan hangisinin bağladığı) |
| `/fills?limit=` | Son dolumlar (en çok 200) |
| `/decisions?limit=` | Son L1 sinyal ve L2 risk kararı olayları (gerekçe kodlarıyla) |
| `/provenance/{fillId}` | Dolum → L2 kararı → L1 sinyali → sleeve → strateji tanımı (`definitionHash`, `structureHash`, DSL) → `manifestHash` → deney kayıtları ve sonuç hash'leri → `DatasetSnapshot` |
| `/radar` | §5 |
| `/system` | Trading ve AI kill switch'leri, son döngü metrikleri, reconciliation, `HoldoutEnvironmentVerified` durumu, son audit olayları (tür, zaman, aktör) |
| `/ai/costs` | `ai.cost_ledger`: ay × görev sınıfı toplamları |
| `/ai/traces?limit=` | `ai.decision_traces` özeti: görev sınıfı, `degraded`, validator sonucu, maliyet; prompt ve yanıt metni yok |

- Paper modunda "OrderIntent" karşılığı L2 olayıdır (before/after notional ve gerekçe). Ekran bunu açıkça etiketler.
- Migration `0008`: yalnızca `GRANT SELECT` ve view. `ops_api`'ye `ai.cost_ledger`, `ai.decision_traces`, `ai.kill_switch_events`, `audit.events`, `research.strategy_versions` okuma izni ve yalnızca dondurulmuş tanımların deney/snapshot bilgisini gösteren `research.frozen_provenance` view'ı. Yeni tablo yok; `research_app`'in ve holdout'un izinleri değişmez.
- Trading.Host Ai projesine referans vermez (mimari kural); AI verisi SQL ile okunur.

### 5. Sinyal radarı
- **Veri:** sembol başına son 320 kapanmış 4h canlı mum (`market.live_candles`). Semboller: aktif run'ların sembolleri ve `Radar:WatchSymbols` (izleme listesi; varsayılan `m3-e1-subset.md`'deki 5 E1 paritesi). İzleme sembolleri yalnızca bu yapılandırmayla canlı mum allowlist'ine eklenir ve hiçbir run'a girmez; emir üretmez.
- **Göstergeler** (Kernel'deki streaming sınıflar, sabit gösterim parametreleri): EMA(50, 100), SMA(150), ATR(14) ve ATR %, RSI(14), Supertrend(10, 3), MACD(12, 26, 9), ADX(14) ve ±DI, Bollinger(20, 2) %B ve bant genişliği, Donchian(50) ve kanala uzaklık, ROC(42), Volume SMA(20) ve son hacim oranı. **BTC rejimi:** BTCUSDT kapanışı ile SMA(300) ve EMA(100) karşılaştırması → `Bull`/`Bear` ve uzaklık %.
- **Yakınlık:** her aktif strateji × sembol için `DslStrategy` mumlarla beslenir; Kernel'e eklenen salt-okunur `Inspect()` son kapanıştaki atomların operandlarını ve doğruluk değerlerini (Kleene `Unknown` dahil) verir. Ekran: "Sinyal aktif", "k/n koşul", karşılaştırma atomu için uzaklık %, `cross_*` için kesişime uzaklık %, `Unknown` için "ısınma/BTC verisi eksik"; pozisyondaysa çıkış koşulu ve efektif stop'a uzaklık.
- `Inspect()` `Step`'in davranışını değiştirmez. M2 hash'leri, M3 golden'ları ve Kohort 0 parity L1/L2 hash'leri regresyon kilididir.
- **Determinizm ve bellek:** radar mumların saf fonksiyonudur; son kapanmış bar zamanına göre tek girişlik önbellekte tutulur. Tahmini bellek birkaç MB'tır (384 MiB sınırı). Hesap istek anında yapılır ve işlem döngüsünü bloklamaz.
- Radar karar üretmez; yalnızca bilgilendirir. Değerleri bir run'ın sinyalleriyle aynıdır, çünkü aynı interpreter kullanılır; çalışan run'ın durumundan bağımsız hesaplanır.

### 6. Claude Code ve doğrulama
- Claude Code dashboard'u gerçek canlı veriyle açmaz ve uç noktalarını çağırmaz. Doğrulama, Testcontainers'taki sentetik veriyle yapılan HTTP testleridir. Gerçek tarayıcı kontrolü kullanıcınındır.
- H24 testi genişletilir: route listesi, yasaklı kelimeler, yeni POST olmaması, her yeni veri uç noktasında tokensız/yanlış token → 401, `/dashboard*` → 200 ve güvenlik başlıkları, yanlış `Host` → 421, `POST /cohorts` → 404.

## Gerekçe
- Tek process, tek port ve build gerektirmeyen statik dosyalar, ADR-0013'ün bellek ve işletim hedefleriyle uyumludur.
- Bearer başlığı ve `sessionStorage`, cookie tabanlı oturumun getireceği CSRF ve kalıcılık risklerini önler.
- Radarın aynı interpreter'ı kullanması, ekrandaki "yakınlık" ile gerçek sinyallerin ayrışmasını önler.

## Reddedilen alternatifler
| Alternatif | Neden |
|---|---|
| SPA framework ve build hattı (React/Vite) | Bağımlılık, build ve tedarik zinciri yüzeyi; tek kullanıcı için gereksiz |
| Ayrı dashboard container'ı | Ek bellek ve ikinci bir yayın portu |
| Cookie tabanlı oturum | CSRF ve oturum yönetimi; loopback'te kazanç yok |
| Radar için ayrı indikatör hesabı | Interpreter'la ayrışma riski |
| Grafana/Prometheus | `architecture.md` §13 |
