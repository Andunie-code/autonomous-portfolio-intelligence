# Belge Değişiklik Kaydı

M0 ile birlikte git'e geçildi; bundan sonra ayrıntılı geçmiş git'te, karar düzeyindeki değişiklikler burada.

## 2026-10-20 — M5 Kapı C: H-ENV, Kohort 0 aktarımı, parity replay ve P1
- **Ortam (kullanıcının koşuları):** bootstrap; deploy (`H20 PASS`); Trading.Host canlı veri olmadan.
- **H-ENV (kullanıcı):** 79 satırın tamamı PASS (H5, H6, H6b, H14, H19–H28; gerçek DB'de H22 dahil). Rapor SHA-256 `df3b9fb0…`. **`HoldoutEnvironmentVerified`** bu rapor hash'i ve çalışan evaluator digest'iyle set edildi ve audit'e yazıldı.
- **Kohort 0 (kullanıcı):** gerçek holdout DB'ye cohort 1 olarak, seed istisnasıyla aktarıldı.
  - `registered_at` 2026-10-15T22:52:33Z, `A_0` 2026-10-17, pencere sonu 2027-10-17;
  - üyeler `ProspectiveSealed` (deneme 1);
  - engine eşdeğerliği kaydı yazıldı (A-2).
- **Parity replay (Claude Code, `research_app`, `R`):** `freeze-import` ve `parity-m5 --record` geçti.
  - L1 `f870c0a6…` ve L2 `b078ebaa…` birebir; 9.844 adım, 16 yeniden başlatma, reconciliation temiz;
  - iki strateji için `Frozen → PaperTrading` yazıldı;
  - L3 farkları ortak sermaye ve portföy drawdown halt'ından kaynaklanıyor.
  - Ayrıntı: `docs/m5-parity-results.md`.
- **P1 (ön kayıtlı, bir kez):** kural, ρ* = 0,70'te **%60 bağlayıcı küme tavanı ve %54 uyarı** öneriyor.
  - Kanıt ince: `policy` koşusunda erken halt, maruziyet %1'in altında, 10–20 işlem.
  - Karar kullanıcıda (P1). Ayrıntı: `docs/m5-p1-analysis.md`.
- **Kalan (Kapı C):** ≥ 7 gün paper, operasyonel raporlar, restart, ağır iş ve kill switch denemeleri, yedek ve geri yükleme, M5 kabul raporu.

## 2026-10-20 — M5 Kapı C: bootstrap düzeltmesi
- **Gözlem (kullanıcının ilk `m5-bootstrap.ps1` koşusu):**
  - yedek, yeniden adlandırma ve sahiplik devri tamamlandı;
  - script `Migrations failed.` ile durdu ve yedek `pg_hba` dosyasını (`172.30.1.1`) yazdı.
- **Neden (T):** `Invoke-OpsMigrations`, `dotnet run` çıktısını pipeline'da bırakıyordu. PowerShell fonksiyonu çıktı satırlarıyla çıkış kodunu birlikte bir dizi olarak döndürdü ve `-ne 0` karşılaştırması bu diziyi her durumda "başarısız" saydı. Asıl yedek `pg_hba` yolunu tetikleyen de bu yanlış alarmdı.
- **Düzeltme:**
  - migrator çıktısı `Out-Host` ile konsola yazılıyor; fonksiyon yalnızca çıkış kodunu döndürüyor;
  - hata olursa PostgreSQL'in son FATAL/ERROR satırları gösteriliyor (secret içermez);
  - kaynak adres tespiti `127.0.0.1`'i atlıyor.
- **Sahiplik devri:** SQL `deploy/postgres/ops-ownership.sql`'e taşındı ve herhangi bir superuser'a ait nesneleri kapsıyor.
- **H-ENV koşusu (kullanıcı):** `m5-henv.ps1`, `docker compose run`'ın stderr bilgi satırında (`Container … Creating`) durdu. Windows PowerShell 5.1'de yönlendirilen native stderr `ErrorRecord`'a dönüşüyor ve `ErrorActionPreference = Stop` onu sonlandırıcı hataya çeviriyordu.
  - Düzeltme: `lib/m5-common.ps1`'e `Invoke-Native` eklendi. stdout'u döndürür, stderr'i konsolda gösterir, başarısızlığı çıkış koduna bırakır.
  - Kapsam: `m5-henv`, `m5-bootstrap` ve `m5-paper-report`'taki tüm `2>$null` kullanımları.
- **H-ENV Ops API kontrolleri:** `m5-henv.ps1`, GET isteğine boş gövde gönderiyordu. `[string]` parametre `$null`'ı `""`'e çeviriyor; Windows PowerShell 5.1 bunu `ProtocolViolationException` ile reddetti. Artık `-Body` ve `-ContentType` yalnızca dolu gövdeyle ekleniyor.
- **Yeni test:** `OpsBootstrapTests`. M0–M4 biçimli veritabanında yeniden adlandırma, login'ler, sahiplik devri ve ardından `ops_owner` ile migration'lar; `research_app` canlı mumları okuyamıyor.

## 2026-10-20 — M5 Kapı B: çevrimdışı kod ve testler (kullanıcı kabulü, 2026-10-20)
Ön kayıt `9dda841`'den sonra. Gerçek ağ erişimi, holdout erişimi, `≥ C` verisi ve AI çağrısı yok (0 USD). Gerçek ortam (bootstrap, H-ENV, seed, paper) Kapı C'de, kullanıcının koşularıyla yapılacak; bu kapıda hiçbir container yeniden oluşturulmadı.

**Eklenenler:**
- **Kernel:**
  - `PortfolioEngine`: adım adım, çok sleeve'li motor; L1/L2 olay akışı ve hash'i; durum hash'i; dolum kayıtları.
  - `RiskConfig` v2 (`ExperimentalPairsMaxExposure` 0,10; `MaxDataLagBars` 2; küme tavanı varsayılan kapalı).
  - Portföy geneli D7 limitleri sleeve sırasıyla.
  - `StepContext`: kill switch, devre dışı sleeve, manuel ve tüm kapatma, bayat veri.
  - `CohortManifestReader`; `CorrelationClusters` (Pearson, ortalama bağlantılı kümeleme, etkin N).
  - `BacktestEngine.Run` artık `PortfolioReplay` döngüsü.
- **Migration 0007 (ops):**
  - `market.live_candles` (`open_time ≥ C` `CHECK`'i) ve anomali tablosu;
  - `research.frozen_strategies`, `research.lifecycle_events` (geçiş kuralları; `PaperTrading` parity kanıtı ister);
  - `trading.*` (koşu, adım günlüğü, olaylar, dolumlar, ledger, bakiye, reconciliation, döngü metriği, kill switch, operasyonel eylemler, `RiskConfig` sürümleri, korelasyon); tümü append-only;
  - `research.candles`, `revealed_cohorts`, `revealed_paper_summary` view'ları;
  - `market_import` ve `ops_api` rolleri; `CONNECT` yalnızca rollere;
  - `research_app`'in `market` şemasındaki geniş okuma yetkisi daraltıldı.
- **MarketData:** `LiveKlineGuardHandler` (yalnızca `data-api.binance.vision/api/v3/klines`, manifesto sembolleri, 1h/4h, sınırlı aralık, redirect reddi, 429/418 tipli hatalar), `LiveKlineClient` (yalnızca kapanmış ve hizalı mum, OHLC, `≥ C`), `LiveCandleStore` (ilk değer korunur, sonradan gelen fark anomali olarak kaydedilir).
- **Execution:**
  - `PaperTrader`: adım günlüğünden yeniden kurma ve hash kontrolü, ledger reconciliation, bayat veri, kill switch, devre dışı bırakma, kapatma eylemleri, V14 (`StrategyHashGuard`), korelasyon izleme;
  - `PaperStore` (Npgsql), `InMemoryPaperStore`, `LiveCandleFeed`, `TradingCycle` (418'de otomatik kill switch), `OpsService`, `ParityReplay` (L1/L2 ve L3).
- **Trading.Host:** saatlik worker (`HH:00:15` UTC) ve Ops API (bearer token; route listesi sabit; manifesto, sonuç ve doğrulama uç noktası yok); sağlık uç noktaları.
- **Research.Host:** `job calibrate-quick` (yük testi) ve `job henv-credentials` (H25).
- **BacktestCli:** `freeze-import`, `parity-m5 [--record]`, `p1-analysis`. P1 öneri kuralı `Research.Evaluation.P1Rule`'da.
- **BackupCli:** AES-256-GCM, 4 MiB'lık doğrulanmış parçalar; hedef klasör kuralı (mutlak, repo ve OneDrive dışı).
- **Evaluator (E1–E10, Kapı A listesi):**
  - E1: komutlar (`migrate`, `register-release`, `release-check`, `henv-selftest`, `henv-mark-verified`, `seed-cohort0`, `export-records`); yapılandırmayla pencere kaynağı (sentetik kaynak yalnızca `holdout_synth*` ve `holdout_henv` DB'lerinde); `CreateDatabase` yolu kaldırıldı;
  - E2: rol başına gerçek login;
  - E3: `ArchiveWindowSource` ve guard'ı;
  - E4: seed'de release eşdeğerliği;
  - E5: migration 0002 (digest'e bağlı ortam bayrağı, `release_equivalences`, koşu ve sonuç kuralları);
  - E6: `evaluator-up.ps1` ve `evaluator-seed-cohort0.ps1` kaldırıldı;
  - E7: `compose.holdout.yml`;
  - E8: H-ENV probe projesi;
  - E9: Dockerfile (probe aynı image'da);
  - E10: testler.
  - H19 öz testi `SelfTest.cs`'te (E1 kapsamında yeni dosya).
- **Ortam:**
  - `docker-compose.yml` yeniden yazıldı: tek `postgres` servisi (eski `dev-db` volume'ü korunur), dört ağ ve statik IP'ler, her serviste `mem_limit`/`cpus`, yalnızca loopback portlar;
  - `deploy/postgres/pg_hba.conf`; `.dockerignore`;
  - Trading.Host ve Research.Host Dockerfile'ları.
- **Script'ler:**
  - kullanıcıya ait: `m5-bootstrap`, `m5-deploy`, `m5-migrate`, `m5-henv`, `m5-seed-cohort0`, `m5-paper-start`, `m5-paper-report`, `m5-backup`, `m5-restore-test`;
  - Claude Code'un kullanacağı: `m5-research` (`research_app`); ortak `lib/m5-common.ps1`;
  - mevcut script'ler bootstrap sonrasında rol bazlı bağlanır; `dev-up.ps1` artık ortam kurmaz.
- **Korkuluklar:** `.claude/settings.json`'a `m5-seed`, `m5-migrate`, servis adlı `docker compose` kalıpları ve `--profile` için deny kuralları; `CLAUDE.md` güncellendi.

**Doğrulama:**
- `verify.ps1` yeşil: **488 test** (Architecture 87, MarketData 97, HoldoutEvaluator 46, Infrastructure 19, Backtest 113, Ai 111, Trading 15); build 0 uyarı/0 hata; secret scan geçti.
- **Regresyon kilidi (gerçek `R`, M0–M4 dev DB):** `m3-parity.ps1`, M2'nin dört hash'ini kodlanmış ve DSL baseline'larıyla birebir üretti. Sentetik kilit: motor değişikliğinden önce yakalanan 36 hash birebir.
- **Kapsanan kontroller:**
  - **Risk tablosu:** deneysel tavanın tam sınırı, kırpma, ret, fiyat hareketinde zorunlu satış yok, çıkışlar serbest, çekirdek limitlerin değişmemesi; portföy geneli 5 pozisyon; diğer sleeve'leri sayan varlık tavanı; küme tavanı.
  - **Operasyonel:** kill switch (bekleyen girişler iptal, stop sürer, yalnızca kullanıcı kapatır); devre dışı bırakma; manuel ve tüm kapatma (`t+1` açılışı); bayat veri (yalnızca çıkış).
  - **Paper ve parity:** her döngüde yeniden başlatılan paper ile kernel arasında L1/L2 olay akışları, durum hash'i ve ledger birebir; restart'ta kaçırılan mumların sırayla işlenmesi; reconciliation bozulmasının tespiti; duplicate dolum 0; `ParityReplay` sentetik veride geçti.
  - **V9, V14.**
  - **Roller (gerçek login'ler):** H6b, H21, H23, H25, H26'nın DB tarafı; lifecycle kuralları.
  - **Ops API:** route listesi (H24); token; gevşetme onayı; cohort bazında PnL.
  - **Canlı istemci:** guard, kapanmış mum kuralı, 418/429.
  - **Evaluator:** digest'e bağlı bayrak, eşdeğerlik, gerçek login'ler, `holdout_henv` öz testi (11/11), arşiv kaynağı (checksum, pencere kesimi, açık pencereye istek atılmaması), kayıt dışa aktarımında mum tablosu olmaması, manifesto hash eşitliği (Trading ve evaluator).
  - **Yapılandırma:** compose limitleri ve ağları (H27 statik), `pg_hba` kuralları, `.dockerignore`, deny kuralları (H28).
  - **Yedek:** çok parçalı şifreleme ve her tür bozulmanın tespiti.

**Protokolden sapmalar ve netleştirmeler (T, gerekçeli; kullanıcı tarafından onaylandı, U 2026-10-20):**
- PostgreSQL ayarları ayrı bir `postgresql.conf` yerine compose'da `-c` bayraklarıyla veriliyor (aynı değerler).
- `pg_hba`'nın loopback satırı `samenet` kullanır. Bootstrap, bu yol çalışmazsa gözlenen kaynak adresi git-ignored `pg_hba.local.conf`'a yazar (§4.3'teki "ölçülür" adımı). Holdout satırları bundan bağımsızdır.
- H25'in kimlik bilgisi kısmı Research.Host'un kendi container'ında `job henv-credentials` ile yapılır.
- H19 öz testi, H16'nın 4. strateji kontrolünde manifesto ayrıştırıcısının reddini (DB'ye ulaşmadan) ret sayar.
- `docs/m5-protocol.md` §11'deki deny listesi genişletildi: `m5-seed`, `m5-migrate`, servis adlı `docker compose` kalıpları, `--profile`. Önceki `docker compose run*holdout*` kalıbı, `--profile` ile başlayan komutları kapsamıyordu.

## 2026-10-19 — M5 başlangıcı ve Kapı A (kullanıcı onayı 2026-10-20)
- **Kullanıcı kararları (U):**
  - M5 başlatıldı; kapılar A, B, C.
  - `portfolio_dev` yedeklenip `ops` olarak yeniden adlandırılacak; Claude Code `ops`'a yalnızca `research_app` ile bağlanacak.
  - Makine dışı yedek harici disk/USB'ye.
  - Evaluator ve `*Holdout*` değişiklikleri yalnızca Kapı A listesindekiler (`m5-protocol.md` §10).
  - Paper raporlarında Claude Code yalnızca operasyonel metrikleri görür.
- **Taslaklar (T):**
  - `docs/m5-protocol.md` v1: paper döngüsü, sleeve'ler, canlı mum kaynağı, operasyonel eylemler, ledger, Ops API, roller, ağlar ve `pg_hba`, parity replay L1/L2/L3, H-ENV prosedürü ve testleri, Kohort 0 seed'i ve engine eşdeğerliği, yedek, Kapı B test listesi, P1 ön kaydı, evaluator değişiklik listesi, asgari paper süresi yöntemi.
  - ADR-0018 (adım adım portföy motoru, `RiskConfig` v2, Execution ve Trading.Host).
  - ADR-0013 revizyon notu.
- **Kapı A onayı (U, 2026-10-20):** belgeler ve A-1…A-6 (E1–E10 dahil) öneri olarak onaylandı: A-1 sleeve modeli; A-2 Kohort 0'ın engine eşdeğerliği bağı; A-3 canlı mum kaynağı; A-4 P1 öneri kuralı; A-5 `holdout_synth`'in korunması; A-6 evaluator dosya listesi.
- **`sources.md`:** S-B9 (`/api/v3/klines`) ve S-B10 (`data-api.binance.vision`, zaman birimi, 429/418). Yalnızca belge sayfaları okundu.
- **Korkuluklar (T17):**
  - `.claude/settings.json`: canlı veri host'ları, kullanıcıya ait M5 script'leri, evaluator/probe ve Trading.Host compose komutları ile holdout'a `psql` için deny kuralları;
  - `CLAUDE.md`: M5 kuralları.
- Kod değişikliği yok.

## 2026-10-19 — HypothesisGeneration v2 pilotu: `Inconclusive (budget)`
- **Spike (8192 token):** `dsl-in-neutral-schema` PASS.
- **Pilot (ön kayıt `13ad935`):** LLM 12/20 (%60), baseline 11/20 (%55); kanıt reddi 1 (v1'de 10); güvenlik ihlali 0; maliyet 0,510653 USD (`docs/m4-hg-pilot-v2-results.md`).
- **Ön kayıtlı kural (tasarım v2 §6, değiştirilmeden):** n = 150; tahmin 150 × 0,02553 × 2 = 7,66 USD > 5,00 USD → yeterlilik koşusu yapılmaz.
  - v2 döngüsünün sonucu `Inconclusive (budget)`. Harness koşmadığı için `TaskQualification` kaydı yok.
  - HypothesisGeneration'da nitelikli model yok; LLM'siz mod sürer.
- **Harcama:** ekim ayı ledger toplamı 0,671812 USD (aylık tavan 10 USD). `docs/m4-ai-reconciliation-2026-10.md` yeniden üretildi (140 çağrı).

## 2026-10-19 — `gemini-3.8-flash` spike'ı (pilot öncesi)
- **Kullanıcının koşusu:** 3/5 PASS; `modelVersion` = `gemini-3.8-flash`.
- **İki başarısızlık spike ayarlarından kaynaklanıyor:**
  - `dsl-in-neutral-schema`: spike'ın 1024 token sınırı thinking ile doldu (`MAX_TOKENS`, 1009 çıktı token'ı). HypothesisGeneration görevinin sınırı 8192 (tasarım v2 §3).
  - `function-calling+structured-output`: model tool'u ikinci turda tekrar çağırdı; senaryo tek tura izin veriyor. HypothesisGeneration görevinde tool yok.
- **Karar (T, öneri):** tasarım v2 değişmeden kalır.
- **Kod:** `spike --max-output-tokens <n>`, senaryoları görevin token sınırında görmek için. `verify.ps1` yeşil.

## 2026-10-19 — HypothesisGeneration v2 döngüsü: tasarım ve kod (pilot öncesi)
- **Kullanıcı kararı (U):**
  - v2 yeterlilik döngüsü; v1 kayıtları değişmeden kalır.
  - Model `gemini-3.8-flash`.
  - Pilot bütçesi 1,00 USD, yeterlilik bütçesi 3,00 USD.
  - Kanıt ve şema netleştirmesi, kullanılmayan indikatörlerin budanması.
  - Baseline'ın tavan etkisinin giderilmesi (hedef bant %10–40).
- **Kod (T):**
  - **Kernel:** `DslNormalizer.PruneUnusedIndicators`. Parser ve ADR-0016 değişmedi; budama yalnızca v2 görevinde, modelin çıktısına uygulanır ve kaydedilir.
  - **Ai:** `EvidenceItem.Metrics`; bir iddia `metric` adı verirse validator o metriği kontrol eder. "Herhangi bir metrikle eşleşme" yolu, kontrolü zayıflattığı için seçilmedi.
  - **Research.Qualification:**
    - `HgItem` varyant 2 (`hg-synthetic v2`, keşif `misfit-v2`);
    - `HgPromptV2` (prompt `hg-synthetic-prompt v2`, şema `f4e85d3d…`);
    - `HypothesisGenerationTask(variant: 2)`.
    - v1 prompt'u, şeması ve set hash'leri birebir aynı; bir testle sabitlendi.
  - **AiCli:**
    - `hg-pilot`, model, set ve bütçeyi tasarım belgesindeki ```` ```json hg-pilot ```` bloğundan okur;
    - protokolde `variant` alanı;
    - `hg-baseline` (çevrimdışı; ağ, veritabanı ve model yok).
- **Baseline kalibrasyonu (çevrimdışı, seed 50000–50049, ilk ve tek deneme):** 17/50 = %34,0, bant içinde. Kurgu ölçümden sonra değiştirilmedi.
- **Tasarım (U, 2026-10-19):** `docs/m4-hg-qualification-design-v2.md` onaylandı; ön kayıt kanıtı, belgenin eklendiği commit.
  - Kullanıcının bütçe esnekliği pilottan önce §6'ya işlendi: tahmin 3 USD'yi aşar ve 5 USD'yi geçmezse iş bütçesi 5 USD olur ve yeterlilik iş payı 0,50'ye çıkar; 5 USD'nin üstünde sonuç `Inconclusive` olur.
  - Pilot seed'leri 60000–60019 (set hash'i `91377be7…`); yeterlilik seed'leri 70000'den başlar.
  - δ = 0,15, α = 0,05 (v1 ile aynı).
- `verify.ps1` yeşil: 416 test.

## 2026-10-19 — M4 Kapı C: yeterlilik koşusu ve M4 kapanışı (kullanıcı kabulü)
- **Yeterlilik koşusu (kullanıcının koşusu; protokol `c434d84`, kayıtlı hash `89075de8…`):** **`Failed`** (`TaskQualification` 1).
  - LLM 22/70 (%31,4), baseline 58/70 (%82,9); tek yönlü Fisher p = 1,0000; güvenlik ihlali 0; maliyet 0,112708 USD; 70 trace.
  - Kanıt reddi 34, geçersiz DSL 4.
  - Ayrıntı: `docs/m4-hg-qualification-results.md`.
- **Sonuç:** HypothesisGeneration'da nitelikli model yok. `qualifiedModels` boş kalır; araştırma LLM'siz modda sürer (D20).
- **Mutabakat:** ekim ayı ledger toplamı 0,146228 USD, 102 çağrı (spike, pilot, yeterlilik). Fatura ay kapanınca girilir (`docs/m4-ai-reconciliation-2026-10.md`). Kill switch hiç tetiklenmedi.
- **Kullanıcı kararı (U):** M4 kapanışı kabul edildi.
- **Güncellemeler:**
  - `roadmap.md`: M4 ✅.
  - `requirements.md` §8: timeout, retry ve eşzamanlılık ölçüldü; Gemini fiyatları doğrulandı; TL/USD mutabakatı aylık.
  - ADR-0007, ADR-0010 ve ADR-0017'nin uygulama durumları.

## 2026-10-19 — M4 Kapı C: HypothesisGeneration pilotu ve yeterlilik protokolü taslağı
- **Pilot (kullanıcının koşusu, ön kayıt `1165601`):** LLM 6/20 (%30), baseline 18/20 (%90); güvenlik ihlali 0; maliyet 0,032664 USD (`docs/m4-hg-pilot-results.md`).
- **Teşhis:** 10 LLM başarısızlığının nedeni kanıt reddi. Model `claims[].value` alanına `M` yerine işlem sayısı, yıl, parite ya da drawdown değeri yazmış; validator doğru reddetmiş. Ön kayıt gereği prompt, şema ve validator değiştirilmedi; bulgu olası bir v2 tasarımının girdisi.
- **Örneklem kuralı (tasarım §6, değiştirilmeden):** kol başına n = 70 (kesin güç 0,84); bütçe tahmini ≈ 0,23 USD.
- **Protokol (U, 2026-10-19):** `docs/m4-hg-qualification-protocol-v1.md` (seed 40000–40069, α = 0,05, δ = 0,15, bütçe 1,00 USD) onaylandı. Kullanıcı, ön kayıtlı kuraldan sapmadan v1 ile devam etmeyi seçti. Belge commit'lendi ve `qualify-register` ile kaydedildi.
- **Kod (T):** AiCli `traces --job <id>`; salt okunur, yalnızca sonuç, neden ve validator mesajları.

## 2026-10-19 — M4 Kapı C: spike geçti; HypothesisGeneration pilot ve yeterlilik altyapısı
- **Spike (kullanıcının koşusu, 2026-10-18T22:18Z):** 5 senaryonun 5'i PASS; gerçek maliyet 0,0008562 USD; gecikme 0,6–2,0 sn (`docs/m4-spike-results.md`).
  - Structured output ile function calling birlikte, `gemini-3.5-flash-lite` üzerinde çalışıyor.
  - `modelVersion` yapılandırılmış adla aynı.
  - DSL, şemada bir string alanı olarak sığıyor.
  - Timeout, retry ve eşzamanlılık değerleri değişmedi.
  - `sources.md` ve ADR-0007 güncellendi.
- **Kod (T):**
  - `FisherExact.Power` ve `RequiredPerArm` (kesin güç; bağımsız hesapla golden testler).
  - `Research.Qualification`: `HgItem` (`hg-synthetic v1`), `HgPrompt` (`hg-synthetic-prompt v1`, şema hash'i `770469e8…`) ve `HypothesisGenerationTask` (LLM kolu ve deterministik grid baseline'ı; değerlendirme ayrı seed'li sentetik evrende, v2 kapılarıyla).
  - AiCli: `hg-pilot`, `qualify-register` (yalnızca commit'lenmiş ve değişmemiş protokol belgesi), `qualify` (harness üzerinden).
  - Sentetik koşular `research.trials`'a yazılmaz.
- **Tasarım ve pilot ön kaydı (U, 2026-10-19):** `docs/m4-hg-qualification-design.md` ve asgari etki δ = 0,15 onaylandı. Ön kayıt kanıtı, belgenin eklendiği commit'tir; pilot o commit'ten sonra koşulur.
- `verify.ps1` yeşil: 410 test.

## 2026-10-19 — M4 Kapı C: ilk spike denemesi ve teşhis iyileştirmeleri
- **İlk spike (kullanıcının koşusu):** 5 senaryonun 5'i FAIL; maliyet 0 USD (ledger'da tüm çağrılar 0 ile settle edildi).
  - `function-calling` senaryolarında HTTP 400 `INVALID_ARGUMENT` görüldü; diğerlerinde aynı başarısızlık "empty output" olarak raporlandı.
  - Kök neden görülemedi, çünkü sağlayıcının hata mesajı ve başarısız çağrının nedeni rapora yazılmıyordu. Bu bir raporlama eksiğiydi; sonuç kaydedildi.
- **Düzeltmeler (T):**
  - `GeminiGuardHandler`, başarısız yanıtın `error.status` ve `error.message` alanlarını alıyor (500 karaktere kısaltılmış). Mesajdaki API anahtarı ve `AIza…` biçimli değerler maskeleniyor.
  - Yanıtlar `Diagnostic` alanı taşıyor (`finishReason`, `blockReason`, parça sayısı).
  - Spike gözlemine her çağrının başarısızlık nedeni ya da tanı ayrıntısı ekleniyor.
  - Gemini'ye gönderilen şemalardan `additionalProperties` çıkarılıyor; `$schema` hiç gönderilmiyor. Bilinmeyen alan kontrolünü yerel validator yapmaya devam ediyor; şema dışı çıktı kabul edilmiyor.
  - `spike --model <id>` ya da `Gemini__Model`: yalnızca fiyat tablosunda bulunan bir model seçilebilir. `gemini-3.8-flash` eklendi (S-G9); varsayılan model değişmedi.
- `.env`'deki anahtar Claude Code tarafından kullanılmadı (CLAUDE.md: `.env` okunmaz; deny kuralı). Spike'ı kullanıcı çalıştırır.
- `verify.ps1` yeşil: 398 test.

## 2026-10-19 — M4 Kapı C başlangıcı: B1 kapandı, spike hazırlığı
- **Kullanıcı kararları (U):**
  - Kapı C başlatıldı.
  - **B1 kapandı:** anahtar ücretli katmanda; bütçe ve harcama limitleri ile ayrı anahtar kuralı konsolda kontrol edildi.
  - **Yeterlilik seti:** HypothesisGeneration pilot ve yeterlilik koşuları, `R` üzerindeki global deneme sayacını (98) şişirmemek için seed'li sentetik evrenlerde yapılacak. Yöntem, Kapı C ön kaydında (`m4-hg-qualification-protocol-v1.md`) gerekçesiyle belirlenir.
- **Model (T):** `gemini-3.5-flash-lite` (D12'nin başlangıç modeli) değişmedi.
  - Kullanıcı, `sources.md`'de doğrulanmış resmi kimliğin kullanılmasını istedi; doğrulanan kimlik budur (S-G1, S-G2).
  - `gemini-4-argon` yalnızca structured output sayfasındaki bir örnekte geçiyor; fiyatı ve durumu doğrulanmadı. Model değişikliği ayrı doğrulama ve karar gerektirir.
- **Adapter düzeltmesi (T, spike öncesi):** model turu (`ProviderContent`) sonraki istekte opak olarak, alındığı gibi geri gönderiliyor. Böylece thought blokları ve imzaları korunur (S-G8). Yeni çevrimdışı test eklendi.
- `sources.md` S-G8: `max_output_tokens` thinking token'larını içerir; Flash-Lite'ta thinking varsayılan olarak minimal.

## 2026-10-18 — M4 Kapı B: çevrimdışı kod, testler ve LLM'siz koşu (kullanıcı kabulü, 2026-10-18)
Ön kayıt `32cf7c5`'ten sonra. Gerçek AI API çağrısı yok (0 USD); gerçek holdout verisi yok; `≥ C` verisi yok.

**Eklenenler:**
- **Kernel:** `FisherExact` (tek yönlü), `DslJson` (DSL v1 serializer; round-trip testli).
- **`src/Modules/Ai`:**
  - sözleşmeler: `IAiProvider`, nötr şema ve tool'lar;
  - konfigürasyon `ai.v1.json`: aylık tavan 10 USD, iş payları %30 ve %5, çağrı tavanı 0,10 USD, fiyatlar S-G2, görev politikaları; karar etkileyen görevlerde nitelikli model yok;
  - `CostLedger` (rezervasyon ve mutabakat), `AiCallGuard` (tavanlar, retry, timeout, circuit breaker, dört otomatik tetik, kill switch watchdog'u);
  - `AiKillSwitch`, `ModelRouter` (`Production` ve `Qualification` modları), `DecisionTrace`, `EvidenceValidator`;
  - yeterlilik: `QualificationProtocol`, harness, `TaskQualification`;
  - mutabakat raporu, spike senaryoları, Npgsql store'ları.
- **`src/Modules/Ai.Gemini`:** `GeminiProvider` (`Google.GenAI` 1.23.0, enjekte edilen `HttpClient`, SDK retry'ı kapalı), `GeminiGuardHandler` (host allowlist'i, redirect reddi).
- **`src/Modules/Researcher`:** altı tool'luk `ResearcherToolbox`, `IResearchToolBackend`, hipotez listesi doğrulayıcısı (liste hash'i), `ApprovedHypothesisSource`, `GridExpansion`, `LlmHypothesisSource` (nitelik yoksa çağrı yok), `ResearchCycle`.
- **Research:** `ResearchToolBackend` (`ExperimentRequestGuard` → deneme tavanı → `no-halt` backtest → deneme ve deney kaydı), `GridSelection` (aday seçimi ve ankore G3), `HypothesisStore`; `TrialRequest.HypothesisId`; `ExperimentStore` arama ve okuma.
- **Migration 0006:** `ai.kill_switch_events`, `cost_ledger`, `decision_traces`, `qualification_protocols`, `task_qualifications`, `research.hypotheses` ve `research.trials.hypothesis_id`. Tümü append-only; kill switch'in otomatik kapatılması ve güvenlik ihlalli bir "Passed" kaydı DB `CHECK`'iyle de reddedilir.
- **Araçlar:** `AiCli` (`killswitch status|trip|clear`, `ledger`, `reconcile`, `spike`). `spike` yalnızca ağ bayrağı ve etkileşimli onayla çalışır; Kapı C'ye ayrılmıştır. BacktestCli'ye `research-m4` komutu eklendi. Script'ler: `scripts/m4-llmless.ps1`, `scripts/m4-ai.ps1`.
- **Korkuluklar:** `.claude/settings.json`'a user-secrets dizini, `user-secrets` komutu ve `Gemini__ApiKey` için deny kuralları; mimari ve repo kuralı testleri.

**Doğrulama:**
- `verify.ps1` yeşil: 393 test (Architecture 59, Ai 93, Backtest 94, MarketData 93, HoldoutEvaluator 38, Infrastructure 16); secret scan geçti.
- Kapsananlar:
  - testing §6.3: nitelikli olmayan modele yönlendirme yok; aylık, iş ve çağrı tavanları; degraded sonuç; refusal ve geçersiz çıktı; kill switch (çağrı yok, uçuştaki çağrının iptali, başka process'in tetiği, Batch iptali, dört otomatik tetik, yalnızca manuel kapatma ve audit); retry (4xx yok, `Retry-After`, en fazla 2, kill switch her retry'dan önce kontrol ediliyor).
  - Researcher sözleşmesi: tam altı tool; izin dışı tool reddediliyor ve kaydediliyor; Researcher projesi yalnızca Kernel ve Ai'ya bağlı, paket yok, kaynak taraması temiz.
  - Gemini adapter'ı: ağsız; tek HTTP isteği (SDK retry'ı yok); host guard'ı.
  - Atomik rezervasyon: 20 eşzamanlı istekten tavana sığan 10'u kabul ediliyor.
  - Fisher golden değerleri, DSL round-trip (502 tanım).
- **Kuru koşu:** gerçek koşudan önce 12 hipotez, seed'li sentetik veride ve atılabilir bir DB'de çalıştırıldı (0 hata). Deneme sayılmaz.

**Gerçek veride LLM'siz koşu (ön kayıtlı, bir kez):** `scripts/m4-llmless.ps1`, engine `32cf7c5+src-tree:d47021c5…`. Kod henüz commit'lenmediği için `src/` ağacının git tree hash'i kaydedildi; commit sonrası `git rev-parse <commit>:src` ile doğrulanır.
- 12 hipotez, 96 deneme; global sayaç 2 → 98; güvenlik ihlali 0; süre 1 dk 8 sn.
- **Aday yok:** 12 hipotezin tamamı reddedildi. Hiçbiri G2'yi (%95 alt sınır > 0) ve G3'ü geçemedi; G6 olasılıkları ≤ 0,11.
- En yüksek `M_R`: H08 (BTC rejim filtreli SMA trendi) %22,8, alt sınır −%9,4.
- Ayrıntı: `docs/m4-llmless-results.md`.

**Teknik kararlar (T):**
- Spike senaryoları sağlayıcıdan bağımsız olarak Ai modülünde.
- Zaman aşımına uğrayan ya da kill switch'le iptal edilen çağrı, tahmini maliyetiyle settle edilir (muhafazakâr); sağlayıcı hatası 0 ile settle edilir (mutabakat yakalar).
- Varsayılan konfigürasyonda çağrı başına tavan 0,10 USD.

**Kapı C'ye kalanlar (B1 gerekli):** gerçek spike, pilot, HypothesisGeneration ön kaydı ve görev uygulaması (`IQualificationTask`), yeterlilik koşusu, mutabakat raporu.

## 2026-10-18 — M4 başlangıcı ve Kapı A (ön kayıt; kullanıcı onaylı)
- **Kapı A onayı (U, 2026-10-18):** protokol v1, hipotez listesi (liste hash'i `89ab3965…`), ADR-0017, kontaminasyon kaydı ve `sources.md` güncellemesi onaylandı. Commit'ten önce `verify.ps1` yeşildi (273 test).
- **Kullanıcı kararları (U):**
  - M4 başlatıldı. Kapılar: A = belgeler; B = çevrimdışı kod, testler ve gerçek veride LLM'siz koşu; C = gerçek Gemini (B1 gerekli).
  - LLM'siz koşuda global deneme sayacı ≤ 100.
  - Hipotezleri Claude Code önerir, kullanıcı onaylar.
- **Ön kayıt (T):** `docs/m4-protocol.md` v1 ve `docs/m4-hypotheses.v1.json`.
  - 12 hipotez ve 96 grid noktası (liste hash'i `89ab3965…`). Deneme sayacı 2 → 98; G6'da N = 98.
  - Aday: grid'de `M_R`'si en yüksek nokta. G3: 2023–2025 için ankore genişleyen pencere. Kapılar `no-halt` koşusunda, v2 eşikleriyle.
  - Köken `dev-assisted`/`grid`; bilgi kesimi 2026-06, tanısal pencereyle çakışma bayrağı.
  - Tanımlar yalnızca `DslParser` ve `DslValidator` ile doğrulandı. `R` üzerinde hiçbir koşu yapılmadı.
- **Yeni ADR-0017:**
  - Projeler: Ai, Ai.Gemini, Researcher ve AiCli.
  - SDK ile enjekte edilen `HttpClient` (host guard'ı, çevrimdışı testler); SDK retry'ı kapalı.
  - `Production` ve `Qualification` router modları.
  - Append-only kill switch ve ledger (migration 0006), `TimeProvider`.
  - Gerçek çağrılar yalnızca `--allow-network` ile, etkileşimli olarak.
- **`sources.md`, Gemini bölümünün 2026-10-18 doğrulaması:**
  - Fiyatlar değişmedi; thinking token'larının çıktı fiyatına dahil olduğu doğrulandı.
  - Tier limitleri ve proje başına uygulama.
  - Ücretli katmanın veri kullanımı.
  - `Google.GenAI` 1.26.1 ve API yüzeyi (`HttpClientFactory`, `CancellationToken`, `UsageMetadata`, `ModelVersion`).
  - Structured output ile tool'ların birlikte kullanımı hâlâ önizlemede ve 3.5 için belirtilmemiş.
- ADR-0007 ve ADR-0010'a not; `roadmap.md` M4 kapıları.

## 2026-10-16 — M3 Kapı D: Kohort 0 ve M3 kapanışı
- **Dev DB:** migration 0004 ve 0005 uygulandı (`scripts/dev-up.ps1`).
- **Kohort 0 manifestosu** (`scripts/m3-cohort0.ps1`, yalnızca `R`):
  - `ema-trend`: M_R −3,54 %, δ 0,05, L_c 12 ay, planlanan işlem 28, gücü yetersiz.
  - `supertrend`: M_R −4,99 %, δ 0,05, L_c 12 ay, planlanan işlem 83, gücü yetersiz.
  - 24 aya kadar hiçbir sürede %80 güce ulaşılamıyor; bu sonuç olduğu gibi kaydedildi.
  - `policy` sonuç hash'leri M2 ile aynı.
  - İki M2 denemesi `research.trials`'e `dev-assisted` olarak işlendi (aile 1 ve 2).
- **Dondurma:** `docs/cohorts/cohort-0.json`, yerel commit `be2bce3`; `registered_at` = 2026-10-15T22:52:33Z (architecture §6.3 istisnası).
- **Sentetik evaluator'da uçtan uca kayıt** (`src/HoldoutEvaluator/evaluator-seed-cohort0.ps1`, `holdout_synth`):
  - Kohort id 1; A_0 = 2026-10-17T00:00Z ve pencere sonu = 2027-10-17T00:00Z sunucu tarafından türetildi.
  - Üyeler `ProspectiveSealed`, deneme sırası 1.
  - Gerçek holdout kaydı M5'te aynı değerlerle seed istisnasıyla yapılır.
- `verify.ps1` yeşil: 273 test (Architecture 43, Backtest 83, MarketData 93, HoldoutEvaluator 38, Infrastructure 16), secret scan geçti.
- **Bilinen açıklar (M5'e devreden):**
  - Evaluator Docker image'ı derlenmedi ve container çalıştırılmadı; manifestoda image digest yerine `local-build:<sha>` var.
  - V9 (operasyonel devre dışı bırakma) ve V14'ün Trading tarafı M5'te test edilir.
  - Gerçek holdout ortamı M5'te kurulur.

## 2026-10-16 — Protokol v2 kalibrasyonu (geçti)
- Ön kayıt `5668d12`'den sonra bir kez koşuldu (`docs/m3-calibration-results-v2.md`): κ = 0,056 (ortalama t = 4,67; hedef 4,65). FP %0, TP %80,0.
- **Atanan araştırma kapısı eşikleri (T, protokol kuralıyla):** `N_min = 100`, `τ_wf = 0,75`, `τ_nb = 0,80`, `τ_dsr = 0,90` (requirements §8 TBD'si kapandı).
- TP hedefin tam sınırında; darboğazlar G4 ve G6.
- Kod: `calibrate --v2`; Kohort 0 araçları (`scripts/m3-cohort0.ps1`, `src/HoldoutEvaluator/evaluator-seed-cohort0.ps1`).

## 2026-10-16 — Protokol v2 ön kaydı (kullanıcı onaylı)
- **Kullanıcı kararları (U):** Protokol v2 onaylandı (`docs/m3-protocol-v2.md`): kapılar `no-halt` koşusunda; DSR'de `V = 1/(T − 1)`; tanısal kural (DD > 0,25 ya da %95 üst sınır < 0 → `Rejected`). Kohort 0, v2 kalibrasyonundan sonra dondurulacak.
- **Birim ve ulaşılabilirlik kontrolü (kullanıcı talimatı):** `N = 100` ve 4 yıllık pencerede G6 en az yıllık Sharpe ≈ 1,9 ister; ≈ %15 etki (yıllık Sharpe ≈ 0,75) teorik olarak geçemez. Doğru pozitif etkisi, hedef t-istatistiği `t* ≈ 4,65` (yıllık Sharpe ≈ 2,3) olarak yeniden tanımlandı.

## 2026-10-16 — M3 Kapı C: E1 alt kümesi (kullanıcı kabulü)
- Kullanıcının etkileşimli koşusu: 3745 sembol dizini, 156 E1 adayı. Seçilenler: WIFUSDT, ORDIUSDT, GMTUSDT, BONKUSDT, BOMEUSDT (5/5, elenen 0). Snapshot `b909f280…`. H1 = 0.
- **Kullanıcı kararı (U):** alt küme, snapshot ve rapor onaylandı. Özet: `docs/m3-e1-subset.md`.

## 2026-10-16 — M3 Kapı B: çevrimdışı kod ve testler
Ön kayıt `9928531`'den sonra. Gerçek ağ erişimi yok; gerçek holdout verisi yok.

**Eklenenler:**
- **Kernel:**
  - İndikatör havuzu (SMA, MACD, ADX, Bollinger, Donchian, ROC, Volume SMA).
  - DSL v1: parser, validator, kanonik biçim, tanım ve yapı hash'leri, yorumlayıcı, Kleene `Unknown`.
  - Motorda BTC referans serisi (`IReferenceSeriesConsumer`).
  - İstatistik: normal dağılım, stationary bootstrap, Holm, Deflated Sharpe, süre ve güç aracı.
  - E1 kategori kuralları ve `StrategyHashGuard` (V14).
- **Research:**
  - Kapılar G1–G6, parametre komşuluğu.
  - Sentetik evrenler ve rastgele DSL üreteci; kalibrasyon koşucusu ve raporu.
  - Aile ve temsilci artefaktları, deneme kaydı (bellek içi ve PostgreSQL).
  - `DatasetCatalog` (H7), `ExperimentRequestGuard` ve audit (H8).
- **Migration'lar:**
  - 0004: `research.trial_families`, `family_structures`, `trials`, `family_artifacts`.
  - 0005: `audit.events`, `research.cohort_status`, `research.strategy_versions`; NOLOGIN roller `research_app`, `trading_app`, `status_relay`.
- **`src/HoldoutEvaluator`:**
  - Holdout şeması: yetkili kohort kaydı, sunucu atamalı `registered_at`/`A_c`, sınırlar, K_D, erken değerlendirme trigger'ı, `Validated` kapısı, `Invalid` ve yeniden değerlendirme kuralları, append-only audit, `validation_status` view'ı.
  - Roller: `eval_registry`, `eval_diagnostic`, `eval_prospective`.
  - Servisler: kayıt, tanısal ve ileriye dönük değerlendirme, status relay.
  - Dar API (`POST /cohorts`, `POST /diagnostic-evaluations`, `GET /cohorts/{id}/status`).
  - Sentetik pencere kaynağı; Dockerfile ve Compose profili `evaluator` (henüz derlenmedi, çalıştırılmadı).
- **MarketData:**
  - E1 alt küme hattı ve raporu.
  - CLI `e1-plan` ve `e1 --allow-network`, `scripts/m3-e1-import.ps1`.
- **BacktestCli:** `parity-m3` (`scripts/m3-parity.ps1`) ve `calibrate`.

**Doğrulama:**
- Gerçek veride (yerel DB, < C) `m3-parity.ps1`: M2'nin dört sonuç hash'i hem C# hem DSL baseline'larıyla birebir üretildi (regresyon kilidi ve parity).
- `verify.ps1` yeşil: build 0 uyarı/0 hata; 272 test (Architecture 43, Backtest 82, MarketData 93, HoldoutEvaluator 38, Infrastructure 16); secret scan geçti. Kapsanan kontroller: indikatör golden ve look-ahead testleri, DSL doğrulama ve hash'ler, DSL ↔ C# parity, BTC rejim filtresi look-ahead testi, H7–H11, H15–H18, V1–V8, V11–V14 (V9: operasyonel devre dışı bırakma M5'te; V10: M6), aile benzerliği (kopya yakalama, 1.000 temsilci, doğrusal bellek), E1 kuralları ve hattı.

**Kalibrasyon (ön kayıtlı, bir kez koşuldu):** hiçbir eşik bileşimi FP ≤ %5 ve TP ≥ %80 şartlarını birlikte sağlamadı. Protokol §7 gereği eşikler gevşetilmedi ve atanmadı. Ayrıntı `docs/m3-calibration-results.md`. Karar kullanıcıda.

**Teknik kararlar (T):**
- M3'te roller NOLOGIN; evaluator `SET ROLE` ile çalışır (gerçek login'ler ve `pg_hba` M5'te).
- Evaluator'da aile anahtarı yapı hash'idir. Benzerlikle aile birleştirme Research tarafında yapılır; evaluator'a iletilmez (bilinen sınırlama).
- DSL `mult` üst sınırı 100 (ADR-0016).
- Kalibrasyonda DSR deneme sayısı N = 100. Gömülü kenar κ = 0,063, ≈ %15 hedefine göre seçildi.
- E1 sıralaması: U0'dan sonraki ilk 180 günün medyan günlük quote hacmi.
- `Validated` kararı doğrulanmamış ortamda `Invalid (unverified environment)` olarak yazılır (architecture §6.5, Invalid nedeni 4).

**Kullanıcı kararı bekleyenler:**
- Kalibrasyon sonucu ve kapıların hangi koşuda değerlendirileceği (Protokol v2).
- Tanısal değerlendirme kuralı önerisi (v1'de tanımlı değildi; kodda öneri olarak duruyor: DD > 0,25 ya da %95 üst sınır < 0 → `Rejected`).

## 2026-10-16 — M3 başlangıcı ve Protokol v1 ön kaydı (Kapı A, kullanıcı onaylı)
- **Kullanıcı kararları (U):** M3 başlatıldı. **D26 (B5 kapandı):** α = 0,05 tek yönlü (aile içinde Bonferroni, kohort içinde Holm), güç 0,80, δ = min(max(0,05; 0,50 × backtest excess); 0,15), δ biçimi (a), ileriye dönük maks. DD > 0,25 → `Failed`, birincil benchmark nakit. **D27:** DSL indikatör havuzu (SMA, MACD, ADX, Bollinger, Donchian, ROC, Volume SMA) ve BTCUSDT rejim filtresi. **D28:** E1 mantığı sentetik testlerle; E1 adaylarından ilk 5 parite kullanıcının Kapı-2 koşusuyla import edilir. Kohort 0 (iki baseline) Protokol v1 onayından sonra dondurulur.
- **Teknik ön kayıt (T; Kapı A kullanıcı onayı 2026-10-16):** `docs/m3-protocol.md` v1 — indikatör formülleri, rejim filtresi ve Kleene `Unknown` semantiği, birincil metrik (`mean(4h log getiri) × 2190`), stationary block bootstrap (`b = ⌈n^{1/3}⌉`, B = 10.000), Holm/Bonferroni uygulaması, süre ve güç aracı (normal yaklaşım; güç tanımı δ koşulunu içermez), araştırma kapıları G1–G6, kalibrasyon protokolü (hedef FP ≤ %5, TP ≥ %80, aday eşik ızgarası ve seçim kuralı), aile normalizasyonu ve global deneme sayacı, Kohort 0 ve E1 kuralları (ADR-0012'deki kesinti istisnası E1'de uygulanmaz).
- **Yeni ADR:** ADR-0016 (DSL v1 beyaz listesi ve şeması; baseline DSL karşılıkları). **Revizyon notları:** ADR-0003 (beyaz liste ADR-0016'da), ADR-0013 (M3'te `holdout_synth` veritabanı, yalnızca sentetik).
- `requirements.md` (D26–D28, B5 kapandı, §8 TBD'ler) ve `roadmap.md` (M3 kapıları ve kapsamı) güncellendi.

## 2026-10-14 — M2 kapanışı (kullanıcı kabulü)
- M2 baseline raporu (`docs/m2-baseline-results.md`) ve M2'nin tamamlanması kullanıcı tarafından onaylandı. İlk dikey dilim (D15: M0–M2) tamamlandı. M3 ayrı onayla başlar.

## 2026-10-14 — M2 uygulaması ve baseline raporu
- **Kod:** `73a6b60` (indikatörler, baseline'lar, maliyet modeli, allocation, backtest motoru, benchmark'lar, deney kaydı, testler). `verify.ps1` yeşil (166 test).
- **Düzeltme:** `452fabd` — benchmark'ın son satış bacağı devirde iki kez sayılıyordu (yalnızca benchmark devir metriği; strateji sonuçları ve hash'leri değişmedi).
- **Sonuçlar:** `docs/m2-baseline-results.md`. Dört koşu `research.experiments`'e kayıtlı (yeniden koşu nedeniyle ikişer kez, aynı hash'lerle). Kabul kriterleri: golden, look-ahead, muhasebe, determinizm (gerçek veride de aynı hash), benchmark analitik eşleşmesi ve allocation tavanı testleri geçti; maliyetler düşülmüş, benchmark karşılaştırmalı rapor üretildi. M2 kullanıcı kabulü bekliyor.

## 2026-10-14 — M2 başlangıcı ve ön kayıt (protokol v1)
- **Kullanıcı kararları (U):** M2 başlatıldı. D25 = %10 (TBD kaydından çıkarıldı). Isınma/boşluk kuralı: boşlukta indikatör sıfırlanır, `L` kesintisiz mumdan önce sinyal yok. Baseline'lar yalnızca 4h'te. Drawdown halt'ı için iki koşu (birincil `policy`, tanısal `no-halt`). Volatilite 4h serisinden (gün sonu kapanışları).
- **Teknik ön kayıt (T):** `docs/m2-protocol.md` v1 — indikatör tanımları, iki baseline (EMA 50/100, Supertrend 10/3), koruyucu stop, olay sırası, maliyet modeli v1 (10 bps ücret, 5 bps açılış ve 20 bps stop slippage), allocation v1 (ters volatilite, 30 gün), portföy riski, benchmark'lar, metrikler, deneme muhasebesi. Ön kayıt kanıtı bu belgenin eklendiği commit'tir; hiçbir backtest bu commit'ten önce çalıştırılmadı.

## 2026-10-14 — M1 veri politikası v1.2 (kullanıcı onaylı)
- **Karar (U, Seçenek A):** Aralık 2021 1h ısınma dosyaları (SOLUSDT, TRXUSDT, SANDUSDT) için M1 verisi ve snapshot `a6c4e89b…` olduğu gibi kalır. M2'den önce tüm paritelere aynı uygulanacak bir ısınma kuralı ön kayıt edilir.
- **Belge:** koddaki `close_time` tutarlılık kuralı politika §6'daki bütünlük hatası listesine yazıldı (kod davranışı değişmedi). Kapanış kaydındaki tutarsızlık böylece giderildi.
- **Kod:** yalnızca snapshot etiketi `m1-data-policy v1.2` (sonraki koşular için); M1 snapshot'ı v1.1 etiketiyle kayıtlı kalır.

## 2026-10-14 — M1 kapanışı (kullanıcı kabulü)
- **Kapı 2 koşusu:** `complete: true`, 20/20. Snapshot `a6c4e89b98364a2e2b3479e81a9e7188ffb27e08a4d444723523e413f7509e82`. Dev DB'de H1 (kesim sonrası mum) = 0 (`m1-import.ps1 -CheckCutoff`, 2026-10-14).
- **Kullanıcı kararları (U):** 20 çekirdek parite, hayatta kalma yanlılığı raporu (LUNA, MATIC, FTM, EOS elendi) ve `REVIEW-PENDING` durumu onaylandı. Her çekirdek paritedeki 1 eksik 1h mum ve 1 doğrulanamaz 4h blok eşikler içinde kabul edildi. Liste: `docs/m1-core-universe.md`.
- **Açık konu (M2 öncesi karar):** SOLUSDT, TRXUSDT ve SANDUSDT'nin Aralık 2021 1h dosyaları (ısınma aralığı) `close_time` kontrolü nedeniyle tümüyle reddedildi; seçim ve araştırma penceresi etkilenmedi.
- **Belge tutarsızlığı (kayıt):** `close_time` tutarlılık kontrolü kodda bütünlük hatası olarak uygulanıyor, ama politika v1.1 §6'daki bütünlük hatası listesinde yazmıyor. Kod politikadan daha sıkı. M2 öncesi ya politikaya eklenmeli (v1.2) ya da ele alınışı değiştirilmeli; ikisi de kullanıcı onayı gerektirir.

## 2026-10-14 — M1 Kapı 2 ikinci deneme düzeltmeleri (teknik)
- **Gözlem:** sembol listeleme çalıştı; ilk ay keşfi, büyük harfli ASCII olmayan bir sembol dizin adında (ad incelenmedi) istek doğrulayıcının yıl öneki kuralına takılıp durdu.
- **Düzeltme (T):** yalnızca `^[A-Z0-9]{2,30}$` biçimindeki sembol dizinleri işlenir; diğerleri atlanır ve raporda yalnızca sayıları verilir (adlar yazdırılmaz).
- **İlerleme çıktısı:** her adımda sayaç satırları (`[Step n/5] …`); mum verisi yazdırılmaz.
- **Önbellek:** doğrulanmış önbellek dosyası ağdan tekrar indirilmez; aynı checksum'la zaten import edildiyse ayrıştırılmaz da. Arşivde bulunmayan dosyalar (404) için önbellekte işaret tutulur ve yeniden sorulmaz.
- **Paralellik ve yeniden deneme:** en fazla 6 eşzamanlı istek; geçici hatalarda (429, 5xx, bağlantı, zaman aşımı) en fazla 2 yeniden deneme, üstel bekleme ve `Retry-After` (en fazla 60 sn). Politika ihlalleri yeniden denenmez. Sıralama ve seçim sonuçları paralellikten bağımsızdır (deterministik sıralama).
- **Doğrulama:** `verify.ps1` yeşil (140 test).

## 2026-10-14 — M1 veri politikası v1.1: S3 listeleme host'u (kullanıcı onaylı)
- **Gözlem:** ilk Kapı 2 denemesinde `data.binance.vision/?prefix=...` S3 XML yerine HTML sayfa döndürdü; listeleme XML ayrıştırmasında durdu. Hiçbir dosya indirilmedi, veritabanına yazılmadı.
- **Karar (U):** `s3-ap-northeast-1.amazonaws.com` yalnızca `data.binance.vision` kovasının S3 listeleme sorguları için allowlist'e eklendi; dosyalar yine `data.binance.vision`'dan alınır. Politika v1.1, `sources.md` S-B8.
- **Kod (T):** istek doğrulayıcı host başına tek rol uygular; listeleme yanıtı S3 `ListBucketResult` değilse (ör. HTML) koşu temiz bir politika hatasıyla durur ve yanıt saklanmaz. Yeni testler: HTML yanıtı, host rolleri.
- **Belge:** `CLAUDE.md`, politika ve `scripts/m1-import.ps1`'e `-ExecutionPolicy Bypass` notu.

## 2026-10-14 — M1 Kapı 1: çevrimdışı kod ve testler
Politika: `m1-data-policy.md` v1.0 (onay commit'i `025f0e2`). Gerçek ağ erişimi yapılmadı.

**Kullanıcı onayı (U, 2026-10-14):** politika §5'teki üç teknik varsayılan (sıralamada listeleme sonrası ilk 30 günün çıkarılmaması, `n_exp`'in arşivdeki ilk 1h mumdan sayılması, 20 tamamlanana kadar tarama) onaylandı. Politika metni değişmedi; onay bu kayıtla belgelenir.

**Eklenenler:**
- Kernel (`MarketData`): `C`/`U`/pencere sabitleri, aralık ve mum üyeliği (UTC, 1M takvim ayı), ms/µs zaman damgası, kline CSV ayrıştırıcısı ve bütünlük kuralları, aylık dosya planlayıcı, kapsam/boşluk analizi, 1h/4h tutarlılığı, sıralama, sınıflandırma, çekirdek seçimi.
- `src/Modules/MarketData`: istek ve yanıt doğrulayan tek çıkış noktası (`ArchiveGuardHandler`), S3 listeleme istemcisi (sayfalama, yıl önekleri), checksum doğrulama, repo ve OneDrive dışındaki önbellek, importçu, olay günlüğü, M1 hattı, kabul raporu.
- Migration 0002: `market.research_cutoff()`, `market.candle_close_time()` (UTC), `market.source_files`, `market.candles` (kesim `CHECK`'i), `market.dataset_snapshots`.
- `src/Tools/MarketDataCli` ve `scripts/m1-import.ps1`: `plan` (ağsız), `check-cutoff` (H1), `run --allow-network` (Kapı 2; etkileşimli onay, yönlendirilmiş girdide reddeder; Claude Code için deny).
- Dışlama listesi `m1-exclusions.v1` (gömülü yapılandırma).

**Doğrulama:** `scripts/verify.ps1` yeşil (build 0 uyarı/0 hata; mimari 37, altyapı 16, MarketData 79 test). Geçen testler H1b, H1c, H1d, H2, H2b, H2c, H3 ve H4'ü kapsar; H1 (dev DB'de kesim sonrası satır yok) CLI'deki `check-cutoff` ile ve konteyner testinde doğrulanır.

**Doğrulanmamış varsayımlar (Kapı 2'nin ilk koşusunda görülecek):** listeleme uç noktası ve S3 XML biçimi, dosya adlandırması, CSV sütun sırası, checksum dosya biçimi, ms/µs geçiş sınırı, 1w/1M dosya içerikleri.

## 2026-10-14 — rev. 5.1: M1 veri politikası v1.0 ve `A_c` kararı (kullanıcı onaylı)
**Kullanıcı kararları (U):**
- **`A_c`:** tek yetkili kaynak `registered_at` (`A_c` = `registered_at` + 24 saatten sonraki ilk UTC gece yarısı). architecture §6.1 ve roadmap M3'teki `F_c` ifadeleri düzeltildi.
- **Kohort 0 istisnası:** yerel commit zamanı `registered_at` kabul edilir; M5'te orijinal `registered_at` ve `A_0` korunarak seed istisnasıyla aktarılır (architecture §6.3, V13).
- **M1 veri politikası:** `docs/m1-data-policy.md` v1.0. Onay, bu belgenin eklendiği commit'in SHA'sına bağlıdır.
- **Kapı modeli (iki aşama):** Kapı 1 = politika ve tüm çevrimdışı kod/testler (açık); Kapı 2 = gerçek ağ erişimi, tek aşamalı etkileşimli onayla.
- **Güvenlik:** ham arşiv dosyaları repo ve OneDrive dışında; ham mum verisi Claude Code bağlamına girmez; host allowlist yalnızca `data.binance.vision`; 3xx reddedilir; veri erişim matrisi (`< U` sıralama ve ısınma, `[U, C)` araştırma, `≥ C` yasak).
- **Seçim:** arşivden deterministik aday keşfi; `n_obs ≥ 90` ve UNKNOWN oranı ≤ %10; quote hacim medyanı; stablecoin, fiat, kaldıraçlı, sarmalanmış ve emtia tokenları dışlanır; elenen adayın yerine sıradaki geçer; 20'ye ulaşılamazsa durulur; hayatta kalma yanlılığı raporu.
- **Kalite:** checksum zorunlu (eksik ya da bozuk checksum reddedilir); 1h ve 4h aynı eşiklere tabi; 1h/4h anomalileri raporlanır, sessizce düzeltilmez.

**Teknik kararlar (T):** politika §5'teki varsayılanlar (karantina günleri sıralamadan çıkarılmaz, `n_exp` çapası 1h, tarama sınırı yok), §6'daki bütünlük hatası tanımları, §7 planlayıcı kuralları, §10 tekrar üretilebilirlik. `.claude/settings.json`'a gerçek ağ bayrağı (`--allow-network`) için deny kuralı.

**B4:** seçim prosedürü ve Kapı 1 onaylandı; Kapı 2 (ağ erişimi) bekliyor.

## 2026-10-14 — rev. 5: yerel tek-makine topolojisi, mum içi stop, aile temsilcisi, normalize parity
Yalnızca belgeler, `CLAUDE.md`, `.claude/settings.json` ve `docker-compose.yml`'deki yorum satırları değişti. Uygulama kodu yok, M1 başlamadı.

**Kullanıcı kararları (U):**
- **D5 (rev.):** VPS yok; tüm sistem kullanıcının yerel bilgisayarında (Windows + Docker Desktop, 16 GB RAM). Hafif mimari: tek PostgreSQL container'ı, servis başına `mem_limit`/`cpus`, iş bazlı Research.Host ve evaluator.
- **D17 (rev.):** Holdout aynı makinede ve aynı PostgreSQL instance'ında ayrı bir `holdout` veritabanında; **kabul edilmiş artık risk.** İzolasyon: roller, `CONNECT`, `pg_hba`, Docker ağları, uygulama katmanı, T17.
- **D24 (yeni):** Ayrı StatusRelay container'ı yok; evaluator `status_relay` rolüyle `research.cohort_status`'a yazar.
- **D11 (rev.):** Kill switch koruyucu stop emirlerini iptal etmez.
- **D25 (yeni):** `RiskConfig`'te bağlayıcı "pozisyon başına maksimum kayıp" hard stop'u; değer TBD (M2).

**Teknik kararlar (T):**
- T1, T2, T3, T17, T19 yerel topolojiye göre yeniden yazıldı (ADR-0013).
- T20: kapanış sinyalleri `t+1` açılışında; mum içi koruyucu stop için tek OHLC kuralı (backtest = paper); Testnet'te borsada stop emri (ADR-0014).
- T21: aile benzerliği yalnızca aile temsilcileriyle (ADR-0015).
- T22: parity replay L1 (sinyal) + L2 (karar) birebir; fark raporu (L3) normalize değerlerle, mutlak emir büyüklükleri karşılaştırılmaz (ADR-0015).
- T23: YAGNI, D15 odağı. T24: model adları konfigürasyondan.

**Yeni ADR'ler:** ADR-0013 (yerel tek-makine topolojisi), ADR-0014 (kapanış sinyali ve mum içi koruyucu stop), ADR-0015 (aile temsilcisi ve normalize parity).
**Değişen ADR'ler:** ADR-0009 "kısmen ADR-0013 ile değiştirildi" (rol, trigger, API ve `Validated` kapısı geçerli); ADR-0001, ADR-0006, ADR-0007, ADR-0010, ADR-0012'ye kısa revizyon notları.

**Kaldırılanlar:** B2 (VPS satın alımı), P4 (VPS SSH için FIDO2), H13 (SSH testi), StatusRelay container'ı, T19'un SSH ve `docker save | ssh` kısmı, VPS sertleştirme (M5).
**Değişen testler:** H5, H21, H25 yerel ortama uyarlandı; yeni H26 (`CONNECT` yetkileri), H27 (kaynak sınırları ve holdout verisinin konumu), H28 (deny kuralları); `testing-strategy.md` §3, §4, §5'e stop, temsilci benzerliği ve L1/L2/L3 parity testleri.
**Yeni engel:** B6 — Testnet anahtarının IP kısıtı yerel ev bağlantısında nasıl uygulanacak (M6).
**Kabul edilmiş artık riskler (ADR-0013):** geliştirici ve Claude Code holdout ile aynı makinede; holdout verisi geliştirme diskinde (Docker volume); runtime anahtarları aynı makinede; uptime kesintileri; tek makine yedek riski.
**Korkuluklar (T17):** `CLAUDE.md` holdout kuralları yerelleştirildi; `.claude/settings.json`'a `docker exec`, `docker compose exec`, `docker cp` deny kuralları eklendi (henüz canlı denenmedi).
**Kapsam dışı bırakılanlar:** `A_c` tanımındaki çelişki (architecture §6.1 ve roadmap M3 ile ADR-0006 arasında) ve M1 veri politikası taslakları bu revizyonda ele alınmadı; açık kalıyor.

## 2026-10-13 — rev. 4.1: `2b0278b` denetiminin düzeltmeleri (kullanıcı onaylı)
Yalnızca belgeler, `CLAUDE.md` ve `.claude/settings.json` değişti. Kod yok, M1 başlamadı.

| # | Bulgu | Düzeltme |
|---|---|---|
| K1 | `results.*` iki evaluator rolü arasında ortaktı; trigger manifesto kaydını da engelliyordu | Ayrı tablolar: `cohort_manifests`, `diagnostic_results` (yalnızca `NotRejected` / `Rejected`), `prospective_runs` ve `prospective_results` (yalnızca `eval_prospective`). `ValidationStatus` yalnızca `prospective_results`'tan türetiliyor. Trigger yalnızca ileriye dönük tablolarda |
| K2 | Research "açığa çıktı" kapısını kendisi yazabilirdi | Ayrı bir `status_relay` container'ı ve `research.cohort_status` tablosu; Research bu tabloyu yalnızca okuyabiliyor |
| O1 | `A_c` istemciden gelebiliyordu; sınırlar yalnızca Registry'deydi | `registered_at` ve `A_c` sunucu tarafından atanıyor; sınırlar holdout DB'sinde `eval_registry` ile uygulanıyor |
| O2 | Zorunlu GitHub push'u; `CLAUDE.md` push'u onaydan muaf gösteriyordu | Yetkili zaman damgası evaluator kaydı; GitHub isteğe bağlı ve onaylı; `CLAUDE.md` düzeltildi; force-push deny kuralları |
| O3 | Güven aralığı tanımsızdı; Failed ve Inconclusive örtüşüyordu | Tek yönlü `(1 − α)` güven sınırları ve düzeltme sırası; kesin karar sırası (drawdown ihlali her zaman Failed); δ'nın uygulanma biçimi Protokol v1'de kullanıcı kararı |
| O4 | Kohort 0 ile Protokol v1'in sırası belirsizdi | Kohort 0, Protokol v1 onayından sonra dondurulur |
| O5 | K_D'nin kapsamı ve "geçti" ifadesi belirsizdi | "Aile başına ömür boyu 1"; sonuç değerleri `NotRejected` / `Rejected` |
| D1 | ops DB'sindeki Registry sürümleri değiştirilebilirdi | Değiştirilemez sürümler; Trading çalıştırmadan önce hash kontrolü yapar |
| D2 | İnsan kanalıyla sızıntı | `CLAUDE.md` kuralı: açığa çıkmamış sonuçlar araştırma girdisi olarak kullanılmaz |
| D3 | Git'teki manifestolar değiştirilebilirdi | `Edit(docs/cohorts/**)` için ask kuralı; force-push deny; yetkili kayıt evaluator DB'si |

Yeni testler: V11–V14, H25. Güncellenen testler: V1, V4, V8.

## 2026-10-13 — rev. 4: holdout stratejisi (kullanıcı kararları U1–U7)
Yalnızca planlama belgeleri değişti; kod yok, M1 başlamadı.

**Yeni ya da değişen kararlar:**
- **D3 (revize):** Testnet doğrulamaya bağlı değil; paper'a girmeden önce dondurma şartı; `Validated` yalnızca ileriye dönük değerlendirmeden gelir.
- **D10 (revize):** Araştırma kesim tarihi `2026-01-01T00:00:00Z`; tanısal holdout yalnızca eler; kohort bazlı ileriye dönük holdout `Validated` durumunun tek kaynağı.
- **D21:** `L_min` = 6 ay, `L_max` = 12 ay (varsayılandır, garanti değildir); gücü yetersiz kohort `Inconclusive`; K_D = aile başına 1 ve dönem başına 10; açık kohort sınırı 2 × 3; aile başına 2 deneme.
- **D22:** E1 ve E2 parite kategorileri; 30 günlük listeleme karantinası; deneysel paritelere %10 toplam maruziyet tavanı.
- **D23:** Operasyonel güvenlik eylemleri her zaman serbest; manifestolar, sonuçlar ve audit geçmişi değiştirilemez.
- **T3–T6 yeniden yazıldı:** zaman semantiği, `market.candles` ve `market.live_candles` ayrımı, kohort ve protokol, dosya planlayıcı.
- **Yeni ADR-0012** (iki izli yaşam döngüsü ve parite kategorileri). **ADR-0006 yeniden yazıldı.** ADR-0009'a roller, DB trigger'ı ve API eklendi.
- `architecture.md` §6–7 yeniden yazıldı; `roadmap.md` M1, M3, M5, M6 ve M8; `testing-strategy.md` §7 (H1–H24, V1–V10); `risk-model.md`; `requirements.md`; `CLAUDE.md`.

**Geçersiz kılınanlar:**
- Dönem bazlı tarihsel birincil holdout (`HoldoutEpoch`, K ve Holm'un dönem bazında uygulanması).
- `Candidate → HoldoutTested → PaperTrading` zinciri.
- M1 incelemesindeki K1/K2 önerisi (`H_end = 2026-10-01` ve ambargo) ve "dünkü güne kadar import" önerisi.
- Önceki plandaki "ileriye dönük veri pencere bitene kadar hiçbir yerde saklanmaz" ifadesi (yanlıştı; koruma erişim kontrolüne dayanır).

**Bloke edici TBD'ler:** kohort içi α, hedef güç, ekonomik δ, drawdown kuralı (M3'te Protokol v1). Bunlar belirlenmeden hiçbir kohort `Validated` olamaz.

## 2026-10-13 — M0 uygulaması (kullanıcı onayıyla)
Kapsam: yerel iskelet, migration altyapısı, loglama/redaction, mimari ve repo kuralı testleri, Claude Code korkulukları, doğrulama script'leri, yerel git ve private GitHub deposu.

**M0'da alınan teknik kararlar (T)**
| Karar | Gerekçe |
|---|---|
| Migration'lar EF Core yerine **düz SQL + küçük bir migrator** (`SqlMigrator`): sürüm sırası, migration başına transaction, checksum, advisory lock | Şema; rol, yetki, view ve `CHECK` kısıtlarına dayanıyor (ADR-0006, ADR-0009). Düz SQL bunları en açık biçimde ifade ediyor ve ek bağımlılık getirmiyor. Uygulanmış bir migration'ın değiştirilmesi hata kabul ediliyor |
| Veri erişim teknolojisi (EF Core, Dapper ya da doğrudan Npgsql) **M1'de** seçilecek | M0'da tablo yok |
| Loglama: `Microsoft.Extensions.Logging` ve kendi `redacting-json` console formatter'ımız. İki katmanlı redaction: config'teki sır değerleri ve bilinen kimlik bilgisi kalıpları | T15. Serilog vb. gereksiz |
| Mimari testler `.csproj` grafiği üzerinden (transitive) | Derleyici kullanılmayan assembly referanslarını düşürür; proje grafiği yasak bağımlılığı tip kullanılmadan da yakalar |
| Holdout sır adları için rezerve tanımlayıcı listesi ve dosya taraması | ADR-0009. Evaluator projesi dışında bu adlar kullanılamaz |
| Test çatısı xUnit 2.9 ve Testcontainers (PostgreSQL 17) | Yaygın ve ücretsiz |
| Barındırılan CI (GitHub Actions) **eklenmedi** | Private repoda dakika kotası ücretlendirilebilir; T16'ya göre yerel doğrulama script'i yeterli. İleride kullanıcı kararıyla eklenebilir |
| Script'lerde regex eşleştirmesi kültürden bağımsız | tr-TR yerel ayarında büyük/küçük harfe duyarsız `[A-Z]` aralığı `I` harfini eşleştirmiyor (M0'da yakalanan hata) |

**M0 doğrulama sonuçları (2026-10-13)**
- `scripts/verify.ps1`: build 0 uyarı ve 0 hata; testler 44/44 geçti (Architecture 28, Infrastructure 16); secret scan geçti. **Temiz klonda** da geçti.
- `scripts/dev-up.ps1`: volume silindikten sonra sıfırdan çalıştırıldı, migration 1 uygulandı; ikinci koşu idempotent (0 yeni). Port yalnızca `127.0.0.1:55432`.
- Kanarya testi: her iki host'un gerçek console çıktısında maskelendi.
- Claude Code deny kuralları **canlı denendi:** `ssh`, `WebFetch(data.binance.vision)` ve `.env` okuma engellendi.
- gitleaks v8.28.0: tüm geçmişte sızıntı yok.
- M0 sırasında testlerin yakaladığı hatalar: `Migration` record'unun `with` ile kopyalanınca eski checksum'ı taşıması; `XDocument.Load(string)`'in yolu URI olarak yorumlaması; tr-TR yerel ayarında regex eşleştirmesi.

## 2026-10-13 — rev. 3 (teknik lider revizyonu)
Yetki: Kullanıcının 2026-10-13 tarihli talimatı. Teknik kararlar teknik lidere devredildi; bütçe, Anthropic, risk limitleri, holdout'un kaldırılması, gerçek para ve satın alma kullanıcıda kaldı.

**Giderilen çelişkiler**
| # | Çelişki | Çözüm | Belge |
|---|---|---|---|
| C1 | "Tek PostgreSQL / yalnızca DB üzerinden iletişim" ile ayrı holdout DB'si | ops DB ve ayrı holdout DB instance'ı; holdout'a yalnızca evaluator API'si üzerinden erişim | ADR-0001 rev., ADR-0009, architecture §1 |
| C2 | M3'te ayrı host ihtiyacı ile D5'te M5'te planlanan VPS | Evaluator M3'te yerelde, sentetik veriyle; gerçek ortam M5'te D5 VPS'inde; `HoldoutEnvironmentVerified` geçidi | ADR-0009, roadmap M3/M5 |
| C3 | Holdout testlerinin "her commit"te çalışması ile gerçek ortam ihtiyacı | H-DEV (her koşu) ve H-ENV (gerçek ortam) ayrımı | testing §1, §7 |
| C4 | Değerlendirme kanalı, ağ ve deploy anahtarlarının tanımsız olması | Dar API, dört Docker ağı, etkileşimli deploy, submit-only token | ADR-0009, architecture §6.2 |
| C5 | İleriye dönük holdout penceresi ile Trading'in canlı veri ihtiyacı | ops DB'sinde `CHECK` yerine `research.candles` view'ı ve yetkiler | architecture §6.2–6.3 |
| C6 | Evaluator'ın internete çıkış ihtiyacı ile internal ağ | Yalnızca evaluator'ın üyesi olduğu ayrı bir `egress` ağı | architecture §1 |
| C7 | Aylık zip dosyalarının holdout mumlarını geliştirme diskine indirmesi | Sınır ayı için günlük dosya kuralı (T6) | ADR-0006, testing H2b |
| C8 | `TrialFamily` tanımının oyuna açık olması | Yapı hash'i, istemci değil sistem hesaplar | ADR-0006 |
| C9 | M4'teki "≥ 10 hipotez" kriterinin yeterlilik örneklemi gibi okunması | Boru hattı kriteri olarak ayrıldı; yeterlilik protokol ve güç analiziyle | roadmap M4, testing §6 |
| C10 | "Hedef: 0" ifadelerinin belirsizliği | Güvenlik değişmezi olarak tanımlandı: tolerans 0 | testing §6.1 |
| C11 | Test edilmemiş fallback politikası | Backtest, paper ve fallback'te aynı politika | ADR-0011 |
| C12 | LLM acil durdurmanın olmaması | `AiKillSwitch` ve otomatik tetikler | ADR-0010, risk §3.2 |

**Kaldırılan ya da değiştirilen onaysız varsayımlar**
- Korelasyon kümesi için %40 tavanı → izleme ve raporlama; bağlayıcı tavan P1.
- "Eşit risk, tavanların yarısı" fallback'i → ters volatilite, onaylı tavanlar (T11).
- Grounding ve URL context → yeniden değerlendirildi; otonom hatta kullanılmıyor (T13), etkileşimli grounding P2.
- Görev sınıfı başına aylık bütçe ve sağlayıcılar arası fallback → kaldırıldı (T14).
- ResultInterpretation'ın "kritik" sayılması → danışma sınıfı (T7).

**Dosyalar:** requirements.md, architecture.md, risk-model.md, testing-strategy.md, roadmap.md ve README.md yeniden yazıldı. ADR-0001, 0005, 0006, 0007 ve 0008 revize edildi. ADR-0009, 0010 ve 0011 ile CHANGELOG.md eklendi. sources.md değişmedi.

## 2026-10-13 — rev. 2
D12 ve D13 geçici olarak işaretlendi; D16 genişletildi; D17–D20 eklendi (kullanıcı onayı). Holdout izolasyonu (M1/M3), ModelRouter ve yeterlilik eklendi. Anthropic referans bilgileri eklendi.

## 2026-10-12 — rev. 1
İlk planlama belgeleri (Faz A–C).
