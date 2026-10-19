# ADR-0013: Yerel tek-makine topolojisi ve hafif kaynak modeli

- Durum: Kabul edildi · Tarih: 2026-10-14
- Karar türü: Kullanıcı kararları D5 (rev.), D17 (rev.), D24; teknik kararlar T1–T3, T17, T19 (rev.)
- Uygulama durumu: **Planlandı, uygulanmadı**
- Yerini aldığı: ADR-0009'un topoloji (madde 1–2), deploy (madde 5) ve gerçek ortam doğrulaması (madde 6) kısımları. ADR-0009'un rol, trigger, evaluator API'si ve `Validated` kapısı kararları geçerlidir.
- İlgili: ADR-0001, ADR-0006, ADR-0009, ADR-0012, ADR-0018, `architecture.md` §1, §6.2, §10, §12

## Revizyon notu (2026-10-19, M5 Kapı A; ayrıntı `m5-protocol.md` §4, §6)
- **`ops` veritabanı (U-M5-2):** `portfolio_dev` yedeklenir ve `ops` olarak yeniden adlandırılır. Kapı C'den sonra Claude Code `ops`'a yalnızca `research_app` login'iyle bağlanır. Owner, migration, trading, import ve yedek işlemleri kullanıcının script'leriyle yapılır.
- **Ağ adresleri (T):** dört ağın sabit alt ağları ve container'ların statik IP'leri vardır. `pg_hba` holdout satırları evaluator'ın statik IP'sine bağlanır. Böylece holdout koruması, Docker Desktop'ta loopback yayınının hangi kaynak adresle geldiğine bağlı değildir. Bu adres Kapı C'de ölçülür ve yalnızca `ops` satırında kullanılır.
- **Superuser (T):** yalnızca container içi yerel soketle bağlanabilir. Tüm `host` satırlarında reddedilir.
- **Evaluator portu (T):** host'a yayınlanmaz (M3'teki `127.0.0.1:55480` kaldırılır; H5). Yönetim komutları kullanıcının `docker compose run` koşularıdır.
- **Ek roller (T):** `ops_owner` ve `eval_owner` (owner ve migration), `market_import`, `ops_api`. Parolalar migration'da değil, bootstrap'ta secret dosyalarından gelir.
- **H19 (T):** gerçek evaluator image'ıyla, gerçek login'lerle ve aynı `pg_hba`'yla, geçici `holdout_henv` veritabanında koşar. Gerekçe: yetkili kayıt değiştirilemez ve açık kohort sınırı 2'dir.
- **`HoldoutEnvironmentVerified` (T):** evaluator image digest'ine bağlanır. Başka bir digest için bayrak geçersizdir; böylece §4'teki "evaluator kodundaki her değişiklik bayrağı sıfırlar" kuralı DB'de uygulanır.
- **Durum:** Kapı A'da onaylandı (U, 2026-10-20); uygulama Kapı B ve C'de.

## Bağlam
- Kullanıcı 2026-10-14'te harici VPS kullanılmamasına karar verdi (D5 rev., B2 kaldırıldı). Tüm sistem kullanıcının Windows + Docker Desktop bilgisayarında çalışacak.
- Makinede toplam 16 GB RAM var; Windows, IDE ve Claude Code da aynı makinede çalışıyor.
- ADR-0009'un güven sınırı, holdout'un geliştirme makinesinden **ağ ve kimlik düzeyinde** ayrılmasına dayanıyordu (ayrı VPS, etkileşimli SSH, ayrı PostgreSQL instance'ı). Bu varsayımlar artık geçerli değil.

## Karar

### 1. Topoloji
Tek makine, Docker Compose:

| Bileşen | Çalışma biçimi | Docker ağları |
|---|---|---|
| PostgreSQL (tek instance) | Sürekli | `ops`, `holdout` |
| Trading.Host | Sürekli (saatlik döngü) | `ops` |
| Research.Host | **İş bazlı** (iş geldiğinde çalışır, boşta bellek tutmaz) | `ops`, `eval` |
| HoldoutEvaluator | **İş bazlı** (zamanlanmış koşu; pencere kapanmadan değerlendirme yapmaz) | `eval`, `holdout`, `egress` |

- Ayrı StatusRelay container'ı **yoktur** (D24). Evaluator, `status_relay` rolüyle `ops` veritabanındaki `research.cohort_status`'a yalnızca ekleme yapar.
- Trading.Host `eval` ağında değildir; Research.Host `holdout` ağında değildir. Hiçbir container portu host'a yayınlanmaz; istisnalar yalnızca `127.0.0.1` (loopback) üzerindendir: geliştirme veritabanı portu ve Ops API/dashboard. SSH tüneli yoktur.

### 2. Tek PostgreSQL instance'ı, ayrı veritabanları
- Veritabanları: `ops` ve `holdout` (M3–M5'te eklenir). M0–M2'de yalnızca mevcut geliştirme veritabanı (`portfolio_dev`) bulunur; `ops` ile ilişkisi M5'te belirlenir (YAGNI).
  - **M3 (rev. 2026-10-16):** holdout tarafının şeması, rolleri ve trigger'ları aynı geliştirme PostgreSQL container'ında ayrı bir `holdout_synth` veritabanında, **yalnızca sentetik veriyle** kurulur ve test edilir. Evaluator `evaluator` Compose profiliyle isteğe bağlı başlar. Gerçek `holdout` veritabanı, `pg_hba` kuralları ve ağ ayrımı M5'tedir.
- `REVOKE CONNECT ON DATABASE ... FROM PUBLIC`; her veritabanına yalnızca ilgili rollere `GRANT CONNECT`.
- Rol ayrımı değişmez: `research_app`, `trading_app`, `market_import`, `ops_api`, `status_relay`, `eval_registry`, `eval_diagnostic`, `eval_prospective` (`architecture.md` §6.2).
- **`pg_hba` kuralları** (ağ düzeyine en yakın katman):
  - `holdout` veritabanına yalnızca `eval_*` rolleriyle ve yalnızca `holdout` Docker ağının alt ağından bağlanılır.
  - `status_relay` rolü `ops` veritabanına yalnızca `holdout` tarafındaki evaluator adresinden bağlanır ve yalnızca `INSERT ON research.cohort_status` yetkisine sahiptir.
  - Host'a yayınlanan loopback port üzerinden `holdout` veritabanına ve superuser'a ağ girişi reddedilir.
- Holdout verisi yalnızca PostgreSQL'in Docker volume'ünde bulunur; repo dizinine (OneDrive altında) hiçbir zaman yazılmaz.

### 3. Kaynak sınırları
Her servis için Compose'da `mem_limit` ve `cpus` tanımlanır. Başlangıç değerleri (M3 ve M5'te ölçülüp ayarlanır, `requirements.md` §8):

| Servis | `mem_limit` | `cpus` |
|---|---|---|
| PostgreSQL | 1 GB (`shared_buffers` buna göre) | 1.0 |
| Trading.Host | 384 MB | 0.5 |
| Research.Host | 1.5 GB | 2.0 |
| HoldoutEvaluator | 1.5 GB | 2.0 |

- Research.Host ve Evaluator aynı anda ağır iş çalıştırmaz; işler kuyruktan (ADR-0002) sırayla alınır.
- Docker Desktop (WSL2) için toplam bellek tavanı kullanıcı makine ayarıdır (`.wslconfig`); öneri: Windows ve IDE için en az 8 GB bırakılması.
- Testcontainers testleri geçici ek PostgreSQL container'ları başlatır; ağır işlerle aynı anda çalıştırılmaz.

### 4. Deploy ve yönetim (T19 rev.)
- Kullanıcı yerelde etkileşimli olarak `docker compose build` / `up` başlatır. Harici image registry ve SSH kullanılmaz.
- Evaluator image digest'i `EvaluatorRelease` olarak kaydedilir. Evaluator kodundaki her değişiklik `HoldoutEnvironmentVerified`'ı sıfırlar.
- Evaluator'ın holdout sırları (veritabanı parolası, submit-only ve read-status token'ları) Docker secret dosyalarında ve evaluator'a ait bir compose/secret dosyasında tutulur. `HoldoutSecretReferenceTests` gereği bu dosyalar `HoldoutEvaluator` adını taşıyan bir dizinde bulunmalıdır; test kodu bu ADR ile değiştirilmez.

### 5. Gerçek ortam doğrulaması (H-ENV, yerel)
`HoldoutEnvironmentVerified`, `testing-strategy.md` §7.2'deki yerel H-ENV testleri gerçek Compose ortamında geçtikten sonra kullanıcı oturumuyla set edilir. Ağ yerine rol, `CONNECT` ve `pg_hba` kontrolleri sınanır. Bayrak yalnızca `Validated` yazımını kapılar; Testnet'i etkilemez (ADR-0012).

### 6. Claude Code korkulukları (T17 rev.)
- `CLAUDE.md`: holdout veritabanına ve evaluator container'ına bağlanılmaz; `docker exec` / `docker cp` ya da volume erişimiyle holdout verisi okunmaz.
- `.claude/settings.json`: `docker exec`, `docker compose exec` ve `docker cp` için deny kuralları; mevcut `ssh`/`scp`/`sftp` deny kuralları korunur.
- Bu kurallar yerel topolojide izolasyonun önemli bir parçasıdır, ama **teknik bir güven sınırı değildir.**

## Kabul edilmiş artık riskler
- **Aynı makine:** Geliştirici ve Claude Code, holdout ile aynı makinede çalışır. Docker yönetim yetkisi olan biri ya da bir araç (`docker exec`, volume bağlama, superuser) rol ve `pg_hba` kontrollerini aşabilir. Önceki VPS tasarımındaki "geliştirme makinesinde holdout verisi yok" güvencesi bu kararla kalkar (D17 rev.).
- **Holdout verisi geliştirme diskinde:** Evaluator'ın import ettiği `C` sonrası veri, aynı diskteki Docker volume'ünde bulunur.
- **Runtime anahtarları:** Testnet ve Gemini runtime anahtarları Claude Code ile aynı makinededir. Process ayrımı (D9) korunur; dosya düzeyindeki ayrım `Read(**/secrets/**)` deny kuralına ve süreç disiplinine dayanır.
- **Exchange anahtarının IP kısıtı:** Ev bağlantısının IP'si değişebilir; açık engel B6.
- **Uptime:** Uyku, yeniden başlatma ve Windows güncellemeleri paper ve Testnet sürekliliğini keser. Bayat veride yalnızca çıkışlar çalışır; Testnet'te koruyucu stop emirleri borsada bekler (ADR-0014). İleriye dönük değerlendirme arşiv verisini kullandığı için uptime'dan etkilenmez.
- **Yedek:** Tek makine; harici kopya şartı korunur (`architecture.md` §12).
- **Saat ve yönetici yetkisi:** Makinenin yöneticisi saati ve tüm verileri değiştirebilir; süreç kuralı ve audit ile ele alınır.

## Alternatifler
| Alternatif | Neden reddedildi |
|---|---|
| VPS (ADR-0009) | Kullanıcı kararıyla iptal (D5 rev.) |
| İki ayrı PostgreSQL instance'ı | 16 GB RAM'de ek bellek; ayrı veritabanı + `pg_hba` aynı makinede benzer pratik ayrımı daha düşük maliyetle sağlar |
| Ayrı StatusRelay container'ı | Ek bellek ve işletim yükü; evaluator zaten durumun kaynağıdır (D24) |
| Research.Host ve Evaluator'ın sürekli çalışması | Boşta bellek tüketimi |
