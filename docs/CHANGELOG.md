# Belge Değişiklik Kaydı

M0 ile birlikte git'e geçildi; bundan sonra ayrıntılı geçmiş git'te, karar düzeyindeki değişiklikler burada.

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
