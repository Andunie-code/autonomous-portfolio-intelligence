# ADR-0024: Hesaplanan alarmlar ve dashboard'un tamamlanması (M9–M10)

- Durum: **Kabul edildi (Kapı A, kullanıcı onayı 2026-11-01)** · Tarih: 2026-11-01
- Karar türü:
  - **U:** `m9-m10-protocol.md` U-M9-1…U-M9-5 (D30 rev., yalnızca Kohort 0'ın terfisi, yeni dış kanal yok, kapılar, kabul edilen sapma; D35).
  - **T:** alarm modeli, katalog ve eşikler (B-1…B-5), H-ENV kaydı, yeni uç noktalar, zincir anahtarı.
- Uygulama durumu: **Kapı B'de çevrimdışı uygulandı (2026-11-01; `verify.ps1` yeşil).** Gerçek ortam kontrolleri kullanıcının Kapı C koşularıdır (`m9-m10-protocol.md` §6).
- Uygulama notları (Kapı B; ayrıntı `m9-m10-protocol.md` §9):
  - Alarmlar `AlarmEvaluator` (saf) ve `AlarmEvaluator.ReadAsync` (`ops_api`, tek sorgu) ile hesaplanır. Migration gerekmedi: gereken bütün `SELECT` yetkileri 0008, 0009 ve 0011'de vardı.
  - `unprotected-position` ve `testnet-reset-suspected`, son döngülerin `degraded` listesindeki kesintisiz seriden hesaplanır (motorun kendi kararı); seri, bayraksız ilk döngüde biter.
  - Zincir, borsa emrinin karar mumuyla sabitlenir (o mum ve önceki iki mumun olayları).
  - Validator ve güvenlik ihlali metinleri API'den dönmez; yalnızca validator adları ve sayılar döner.
- İlgili: ADR-0010, ADR-0013, ADR-0018, ADR-0020, ADR-0021, ADR-0023; `m9-m10-protocol.md`; `architecture.md` §3.4, §11; `risk-model.md` §3–§4

## Bağlam
- `architecture.md` §11 "Alarmlar M10'da" der. Bugün alarm niteliğindeki her durum dağınık bir kayıttır: `trading.kill_switch_events`, `ai.kill_switch_events`, `audit.events` (`protective_stop_failed`, `testnet-reset-suspected`, `strategy_hash_mismatch`, `cycle_error` …), `trading.cycle_metrics`. Kullanıcı bunları dashboard'un farklı sekmelerinde ya da script çıktılarında görür.
- Bütçe için erken uyarı yoktur; aylık tavan yalnızca aşılınca AI kill switch'i açar.
- H-ENV'in aylık koşusu (testing-strategy §1) hiçbir yerde izlenmez. `HoldoutEnvironmentVerified` holdout veritabanındadır ve `ops_api` onu okuyamaz (ADR-0020); bayrağın zaman bileşeni de yoktur.
- Sistem tek yerel makinede çalışır (ADR-0013). Makine kapalıyken hiçbir yerel bileşen bildirim gönderemez.
- M9a (ADR-0020) zinciri yalnızca paper dolumundan açar. M6'nın borsa emirleri ve M8'in allocator olayları zincire bağlanmamıştır.

## Karar

### 1. Alarmlar hesaplanır, saklanmaz
- Alarm, mevcut tabloların istek anındaki saf fonksiyonudur (`AlarmEvaluator`, Execution). Ayrı alarm tablosu, alarm yazan arka plan işi ve onay/susturma durumu yoktur.
- Okuma yolu: `GET /alarms` (token, salt okunur, ADR-0020 §3 güvenlik modeli) ve dashboard alarm şeridi.
- **Yeni dış kanal yok (U-M9-3):** e-posta, webhook, mesajlaşma ya da işletim sistemi bildirimi eklenmez. Yeni sır, ağ çıkışı ve tedarik zinciri yüzeyi oluşmaz.
- Execution AI'sız kalır: AI tavanı Trading.Host'ta `AiConfiguration.LoadDefault()` ile okunup değer olarak verilir; `ai.*` tabloları, M9a'da olduğu gibi `ops_api`'nin `SELECT` yetkisiyle SQL'den okunur.

### 2. Katalog ve eşikler
`m9-m10-protocol.md` §3.2: A1 trading kill switch, A2 AI kill switch, A3 bütçe (%80 / %100), A4 otomatik AI tetiği (7 gün), A5 H-ENV tazeliği (31 gün), A6 heartbeat (75 dk), A7 Testnet ve bütünlük alarmları (korumasız pozisyon, takılı `Unknown`, reset şüphesi, reconciliation, hash uyuşmazlığı, döngü hatası).

### 3. H-ENV koşusunun ops'a kaydı
- `m5-henv.ps1` bitişte `ops.audit.events`'e `henv_run_recorded` yazar: `verdict`, satır ve hata sayısı, rapor SHA-256'sı, evaluator digest'i.
- Bu, bir **koşu** kaydıdır; doğrulama bayrağı değildir. `HoldoutEnvironmentVerified` ve `henv-mark-verified` değişmez; `Validated` yazımının kapısı yine holdout'taki bayraktır.
- Holdout veritabanından hiçbir veri ops'a taşınmaz; adında `Holdout` geçen dosya değişmez.

### 4. Dashboard tamamlaması
- **Zincir anahtarı:** ortak zincir kurucusu `(run_id, sleeve, symbol, decision_bar)` ile tam eşleşir; paper dolumunun 8 saatlik penceresi yerine geçer.
- **Yeni GET:** `/provenance/order/{id}` (`id`, `client_order_id` ya da `exchange_order_id`) ve `/alarms`. Rota adları H24'ün yasaklı alt dizgilerini içermez; `OpsApi.Routes` pin listesi bilinçli olarak güncellenir.
- **Allocator düğümü:** `AllocatorAdjusted`/`AllocatorIgnored`/veto olayları ve `trading.allocator_proposals` kaydı zincirde gösterilir; `task_id` DecisionTrace'e bağlanır.
- **Görünürlük:** `/ai/traces` `requested_model`, token ve tahmini maliyeti döner; `/ai/costs` ay ara toplamı, görev sınıfı toplamı, iş türü kullanımı ve `monthlyCapUsd` döner; kill switch kartları aktör, neden ve zamanı gösterir.
- Yeni POST yoktur. AI kill switch'in kapatılması yalnızca CLI'dadır (`architecture.md` §3.4 ve `risk-model.md` §3.2 düzeltilir).

## Gerekçe
- Hesaplanan alarm, kaynak kayıtla çelişemez ve "alarm kaydı yazılamadı" diye yeni bir hata sınıfı doğurmaz.
- Tek kullanıcılı, yerel ve çoğu zaman gözetimli bir sistemde pull görünümü yeterlidir; makine kapalıyken push kanalı da çalışmayacağından asıl risk (kesinti) yine A6 ve açılıştaki reconciliation ile ele alınır.
- H-ENV'in ops'a yalnızca koşu kaydı olarak yazılması, holdout güven sınırını (ADR-0009, ADR-0013) değiştirmeden "aylık" şartını ölçülebilir kılar.

## Reddedilen alternatifler
| Alternatif | Neden |
|---|---|
| E-posta, webhook ya da mesajlaşma botu | Yeni sır, ağ çıkışı ve dış servis; makine kapalıyken zaten çalışmaz (U-M9-3) |
| Windows bildirimi için zamanlanmış görev | Ek kurulum; kullanıcı mevcut kanalları seçti (U-M9-3) |
| Kalıcı alarm tablosu ve onay/susturma | Kaynakla çelişme riski ve yeni yazma yolu; tek kullanıcı için gereksiz |
| Prometheus/Grafana | Bellek ve işletim yükü (ADR-0013); ops tabloları yeterli (`architecture.md` §11) |
| `ops_api`'ye holdout bayrağını okuma yetkisi | Holdout güven sınırını genişletir (ADR-0013) |
| AI kill switch için Ops API POST'u | ADR-0020'nin "yeni POST yok" kuralı; CLI yeterli ve audit'li |

## Sonuçlar
- Execution'a `Alarms.cs` ve zincir kurucusu, Trading.Host'a iki GET rotası ve dashboard değişiklikleri, `m5-henv.ps1`'e bir audit satırı, yeni `scripts/m10-alarm-drill.ps1` ve `docs/runbook.md` eklenir.
- Migration yalnızca eksik bir `SELECT` yetkisi çıkarsa eklenir (yalnızca `GRANT SELECT`).
- Makine kapalıyken alarm iletilemez; bu ADR-0013'ün kabul edilmiş artık riskidir.
