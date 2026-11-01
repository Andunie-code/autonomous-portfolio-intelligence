# M9–M10 Protokolü — Dashboard tamamlaması, alarmlar, runbook ve 30 günlük Testnet provası (v1)

- Durum: **Ön kayıt — kullanıcı tarafından onaylandı (Kapı A, U, 2026-11-01; §0.1 U-M9-1…U-M9-5 ve §0.2 B-1…B-5 dahil).** Ön kayıt kanıtı, bu belgenin onaylandığı git commit'idir. O commit'ten önce M9/M10 kodu yazılmaz.
- Karar türleri: **U** = kullanıcı, **T** = teknik lider, **P** = öneri (kullanıcı onayı gerekir).
- İlgili: ADR-0010, ADR-0013, ADR-0018, ADR-0020, ADR-0021, ADR-0023, **ADR-0024**; `m6-protocol.md` v1.2; `m8-protocol.md`; `architecture.md` §3.4, §11; `risk-model.md` §3–§4; `testing-strategy.md` §1, §5, §6.3; D3, D4, D11, D30 (rev.), D32, **D35**.
- Değiştirilmeyenler:
  - `RiskConfig` limitleri (D7, D22, D25, D29), maliyet modeli v1, allocation politikası T11 ve allocator bandı;
  - M2'nin dört sonuç hash'i, M3 golden'ları, Kohort 0 ve Kohort 1 manifestoları ile parity L1/L2 hash'leri;
  - paper run'ları ve allocator modu (gölge);
  - evaluator ve adında `Holdout` geçen dosyalar (değişiklik listesi **boştur**);
  - `research_app` izinleri; dashboard'da eylem düğmesi yoktur ve Ops API'ye yeni POST eklenmez.

## 0. Kullanıcı kararları

### 0.1 Alınmış kararlar (U)
| # | Karar |
|---|---|
| U-M9-1 (D30 rev., 2026-10-30) | `PaperTrading → TestnetActive` asgari paper süresi **≥ 10 takvim günü** (önceki 14). ≥ 60 işlenmiş 4h mum ve son 7 günde sıfır kritik hata şartları değişmez. Gerekçe: 60 × 4 saat = 10 gün; mum şartı süreyi zaten bağlar. Geçiş yine kullanıcı oturumu ve onayı ister (D3, V10). `m6-protocol.md` v1.2 |
| U-M9-2 (2026-11-01) | Kapı C'de **yalnızca Kohort 0** (`ema-trend`, `supertrend`) `TestnetActive` olur (M6 C4). Kohort 1 yeni eşikle uygun olsa da terfisi ayrı kullanıcı kararıdır |
| U-M9-3 (2026-11-01) | Alarmlar için **yeni dış bildirim kanalı açılmaz** (e-posta, webhook, toast yok). Alarmlar mevcut verinin hesaplanmış görünümüdür: Ops API `GET /alarms`, dashboard alarm şeridi ve audit (ADR-0024) |
| U-M9-4 (2026-11-01) | M9 ve M10 birlikte yürütülür. Kapılar: **A** = bu protokol, ADR-0024, D30 rev. belgeleri, T17; **B** = çevrimdışı kod ve testler, `verify.ps1` yeşil, API maliyeti 0; **C** = kullanıcının gerçek ortam koşuları (§6) |
| U-M9-5 (2026-11-01) | **Kabul edilen sapma (M5/M8 deseni):** M10'un "30 günlük Testnet provası tamamlandı" kriteri, provanın başlatılması ve ilk döngülerinin doğrulanmasıyla kapanır; 30 günlük gözlem arka planda sürer ve sonuçları `m10-acceptance-report.md`'ye ek olarak işlenir |

### 0.2 Kapı A teknik değerleri (T; kullanıcı onayıyla bağlayıcı)
| # | Konu | Değer | Bölüm |
|---|---|---|---|
| B-1 | Bütçe uyarısı | Bu UTC ayında gerçekleşen + rezerve harcama ≥ aylık tavanın **%80**'i → uyarı; ≥ **%100** → kritik | §3.2 |
| B-2 | H-ENV tazeliği | Son PASS H-ENV kaydı **31 günden** eski ya da hiç yok → uyarı; son kayıt FAIL → kritik | §3.2 |
| B-3 | Heartbeat | Son döngü kaydı **75 dakikadan** eski → kritik (döngü saatliktir, HH:00:15 UTC) | §3.2 |
| B-4 | Takılı `Unknown` emir | `Unknown` durumunda **60 dakikadan** uzun kalan emir → kritik | §3.2 |
| B-5 | Pencere | "Son 7 gün" alarmları `Promotion.ErrorWindow` (7 gün) ile aynı pencereyi kullanır | §3.2 |

## 1. Kapsam ve sınırlar
- **M9:** ADR-0020'nin (M9a) üç kriterinin M6 ve M8 verileriyle tamamlanması (§2).
- **M10:** alarm kataloğu, `GET /alarms`, alarm tatbikatı, runbook ve 30 günlük Testnet provası (§3–§5).
- **D30 rev.:** yalnızca `Promotion.MinimumDays` ve ilgili metin ve testler (`m6-protocol.md` v1.2 §14).
- Claude Code gerçek ortamda dashboard'u açmaz, `/alarms` dahil hiçbir veri uç noktasını çağırmaz, `m10-*.ps1` script'lerini çalıştırmaz. Doğrulama Testcontainers'taki sentetik veriyle yapılır (ADR-0020 §6).

## 2. M9 — Dashboard tamamlaması
Keşif (2026-11-01, salt okuma) ve giderilecek eksikler:

### 2.1 Kriter 1 — emirden veri setine zincir
| Eksik | Çözüm |
|---|---|
| Zincir yalnızca `fillId` ile açılıyor; Testnet borsa emrinden açılamıyor | Yeni `GET /provenance/order/{id}`: `id`, `client_order_id` ya da `exchange_order_id` ile çözülür. Yanıt: emir düğümü (amaç, tip, durum geçmişi, `clientOrderId`, `exchangeOrderId`, borsa dolumları ve trade id'leri), varsa bağlı `trading.fills` satırı ve zincirin geri kalanı |
| Zincir kararı 8 saatlik pencereyle buluyor | Ortak zincir kurucusu `(run_id, sleeve, symbol, decision_bar)` anahtarıyla tam eşleşir. Testnet emrinde `decision_bar` emirden, paper dolumunda dolum adımından türetilir |
| Testnet dolumu borsa emrini göstermiyor; `/fills` paper ve Testnet'i ayırmıyor | `/provenance/{fillId}` Testnet dolumunda bağlı borsa emrini verir; `/fills` venue ve run sütunu döner |
| Testnet "Son emirler" tablosu tıklanamıyor; `exchange_order_id` dönmüyor | `recentOrders` `exchange_order_id` döner; satır zinciri açar; İzlenebilirlik sekmesindeki kutu emir kimliğini de kabul eder |
| Allocator çarpanı zincirde ve `/decisions`'ta yok | Zincire `allocator` düğümü: o bardaki mod, `AllocatorAdjusted` (T11 ağırlığı → uygulanan ağırlık, çarpan), `AllocatorIgnored`, `allocator-veto`, `trading.allocator_proposals` kaydı ve `task_id` → DecisionTrace bağlantısı. `/decisions` allocator olaylarını da listeler |

Zincir: borsa emri / dolum → `OrderIntent` karşılığı L2 olayı (+ allocator düğümü) → L1 sinyali → sleeve → `StrategyDefinition` (`definitionHash`, `structureHash`) → `manifestHash` → deney kayıtları → `DatasetSnapshot`.

### 2.2 Kriter 2 — DecisionTrace, degraded, kill switch'ler
- `/ai/traces` ek alanlar: `requested_model`, `input_tokens`, `output_tokens`, `estimated_usd`, güvenlik ihlali sayısı, `prompt_version`, `job_id`, `reason`. Prompt ve yanıt metni **dönmez** (değişmez).
- Tablo: "istenen → gerçek model", `degraded` rozeti, validator özeti (geçen/kalan kontrol sayısı; ham JSON yerine), gecikme, token, tahmini ve gerçek maliyet.
- `/system` ve kartlar: trading kill switch (durum, aktör, neden, zaman) ve AI kill switch (durum, tetik türü, aktör, neden, zaman).

### 2.3 Kriter 3 — görev sınıfı bazında maliyet ve bütçe
- `/ai/costs`: ay × görev sınıfı × iş türü satırları (mevcut), ay ara toplamı, görev sınıfı bazında tüm zamanlar toplamı, bu ay iş türü bazında kullanım ve pay tavanı (`jobShares`), `monthlyCapUsd`.
- Tavan `ai.v1.json`'dan Trading.Host tarafından `AiConfiguration.LoadDefault()` ile okunur ve sorguya parametre olarak verilir; Execution AI'sız kalır (ADR-0023 §1).
- UI: bu ayın bütçe çubuğu (`meter()`), %80'de uyarı, %100'de kritik rengi.

## 3. M10 — Alarmlar (ADR-0024)

### 3.1 Model
- Alarm, mevcut tablolardan **istek anında hesaplanan** bir durumdur; ayrı alarm tablosu ve yeni yazma yolu yoktur. Hesap saf bir fonksiyondur (`AlarmEvaluator`, Execution modülü) ve girdisi `ops_api`'nin zaten okuyabildiği verilerdir.
- `GET /alarms` (token, salt okunur): her etkin alarm için `code`, `severity` (`critical` / `warning`), `since`, kısa açıklama ve runbook bölümü. Fiyat, bakiye, PnL ve secret içermez.
- Dashboard: her sekmenin üstünde alarm şeridi; "Sistem ve AI" sekmesinde alarm tablosu.

### 3.2 Katalog
| Kod | Koşul | Önem | Kaynak |
|---|---|---|---|
| A1 `trading-kill-switch` | Trading kill switch açık (manuel ya da HTTP 418) | kritik | `trading.kill_switch_events` |
| A2 `ai-kill-switch` | AI kill switch açık; tetik türü gösterilir | kritik | `ai.kill_switch_events` |
| A3 `ai-budget` | Bu ay gerçekleşen + rezerve ≥ %80 (uyarı) / ≥ %100 (kritik) | B-1 | `ai.cost_ledger`, `ai.v1.json` |
| A4 `ai-auto-trip` | Son 7 günde otomatik AI tetiği (`SpendVelocity`, `CostDeviation`, `CircuitBreaker`, `MonthlyCap`) | uyarı | `ai.kill_switch_events` |
| A5 `henv-stale` | Son PASS H-ENV kaydı 31 günden eski ya da yok (uyarı); son kayıt FAIL (kritik) | B-2 | `audit.events` (`henv_run_recorded`) |
| A6 `heartbeat` | Son döngü 75 dakikadan eski (makine, Docker ya da host kapalı) | kritik | `trading.cycle_metrics` |
| A7 `unprotected-position` | Korumasız Testnet pozisyonu > 0 | kritik | Testnet sayaçları (`/testnet` ile aynı) |
| A7 `order-unknown` | `Unknown` emir 60 dakikadan uzun | kritik | `trading.exchange_orders` |
| A7 `testnet-reset-suspected` | Sonrasında `rebaseline` yapılmamış reset şüphesi | kritik | `audit.events` |
| A7 `reconciliation` | Son 7 günde başarısız reconciliation | kritik | `trading.reconciliation_runs` |
| A7 `strategy-hash-mismatch` | Son 7 günde hash uyuşmazlığı | kritik | `audit.events` |
| A7 `cycle-error` | Son 24 saatte `error` sonuçlu döngü | uyarı | `trading.cycle_metrics` |

- A6 makine kapalıyken kimseye ulaşamaz; açılışta ilk sayfa yüklemesinde kesintiyi gösterir. Bu, ADR-0013'ün kabul edilmiş artık riskidir (§3.4).
- **H-ENV kaydı:** `scripts/m5-henv.ps1` bitişte `ops` veritabanına `audit.events` satırı (`henv_run_recorded`, aktör `user`) yazar. Payload yalnızca `verdict` (PASS/FAIL), satır ve hata sayısı, rapor SHA-256'sı ve evaluator digest'idir. Holdout verisi, değer ve sır adı içermez. `HoldoutEnvironmentVerified` bayrağı değişmez ve hâlâ yalnızca holdout'tadır; A5 yalnızca koşunun **yapıldığını** izler.
- **Belge düzeltmesi:** AI kill switch'in kapatılması yalnızca CLI ile yapılır (`m4-ai.ps1 killswitch clear`); `architecture.md` §3.4 ve `risk-model.md` §3.2'deki "Ops API veya CLI" ifadesi "CLI" olur. Ops API'ye yeni POST eklenmez (ADR-0020 §2).

### 3.3 Kapı B test listesi (bağlayıcı)
- Her alarm için tetiklenme ve temizlenme (sentetik veri, Testcontainers, enjekte saat):
  - A3: %79,9 → yok, %80 → uyarı, %100 → kritik; geçen ayın harcaması sayılmaz;
  - A5: kayıt yok → uyarı; 31 gün − 1 sn → yok; 31 gün + 1 sn → uyarı; son kayıt FAIL → kritik;
  - A6: 75 dk − 1 sn / + 1 sn;
  - A4: her otomatik tetik türü; manuel tetik A4 üretmez; 7 gün sınırı;
  - A1 ve A2: aç → alarm, kapat → temiz;
  - A7: korumasız pozisyon, 60 dk sınırında `Unknown`, reset şüphesi ve sonrasında rebaseline, reconciliation hatası, hash uyuşmazlığı, döngü hatası.
- `/alarms`: tokensız ve yanlış token → 401, yanlış `Host` → 421, H24 yasaklı alt dizgileri yok, yanıtta secret yok; `OpsApi.Routes` pin listesi bilinçli olarak güncellenir.
- Mevcut `Ai.Tests` otomatik tetik testleri değişmeden geçer.
- `RepositoryRuleTests`: `*-File*m10-*` deny kuralı. `HoldoutSecretReferenceTests` yeni script'i de tarar.

### 3.4 Alarm tatbikatı — `scripts/m10-alarm-drill.ps1` (yalnızca kullanıcı)
`Confirm-Interactive`; yönlendirilmiş girdi reddedilir. Rapor `Get-ReportDirectory` altına JSON olarak, SHA-256'sıyla yazılır; yalnızca operasyonel alanlar (alarm kodları, önem, sayılar, zamanlar).
1. Başlangıç: `/alarms` ve kill switch durumları.
2. **Trading kill switch:** `POST /kill-switch` aç → A1 görünür; Testnet'te açık giriş emirleri iptal, koruyucu stop sayısı değişmez (yalnızca sayılar) → kapat → A1 temizlenir. M6 C4'ün kill switch denemesini de karşılar.
3. **AI kill switch:** `m4-ai.ps1 killswitch trip` → A2 görünür; bir sonraki allocator kararı LLM çağrısı olmadan T11'e düşer → `killswitch clear` (`EVET`) → A2 temizlenir. Pilot sayaçlarında bu süre `kill-switch` fallback'i olarak görünür (operasyonel etki, beklenen).
4. **Bütçe:** mevcut yüzde ve eşikler yazdırılır. Gerçek harcama **yapılmaz**; tetiklenmesi §3.3 testleriyle kanıtlanır.
5. **H-ENV:** A5 durumu. Gecikmiş ya da kayıt yoksa kullanıcı `m5-henv.ps1`'i çalıştırır; tatbikat tekrar çalıştırılınca A5'in temizlendiği görülür.
6. **Heartbeat:** gerçek ortamda zorlanmaz (Trading.Host'u durdurmak provayı keser); §3.3 testleri ve ilk makine uykusu sonrası gözlem.

## 4. Runbook — `docs/runbook.md` (Türkçe)
1. Alarm → eylem tablosu (A1–A7).
2. Bant dışı anahtar iptali ve yenileme: Binance Testnet (sitede anahtarın silinmesi, `m6-testnet-key.ps1 -Clear`, yeni anahtar) ve Google Cloud / Gemini (konsolda iptal, `m4-ai.ps1 killswitch trip`, `m8-update.ps1 -ClearKey` / `-SetKey`).
3. Uyku, kapanma ve yeniden başlatma sonrası devam: Docker Desktop otomatik başlatma, `restart: unless-stopped`, ilk döngüde reconciliation ve catch-up, `m6-update.ps1 -ChecksOnly`, A6.
4. Docker Desktop (WSL2) `.wslconfig` bellek ayarı (ADR-0013) ve servis bellek ölçümü (`requirements.md` §8 TBD).
5. Aylık takvim: H-ENV (`m5-henv.ps1`), yedek ve geri yükleme testi (`m5-backup.ps1`, `m5-restore-test.ps1`), Testnet sıfırlaması → `m6-testnet-activate.ps1 -Rebaseline`, AI fatura mutabakatı (`m4-ai.ps1 reconcile`).
6. 30 günlük prova kontrol listesi.

## 5. 30 günlük Testnet provası
- **Başlangıç:** Kohort 0'ın iki stratejisinin `TestnetActive` olduğu ve ilk Testnet döngüsünün `-ChecksOnly` ile temiz göründüğü an (C2).
- **Gözlem (yalnızca operasyonel alanlar, `m6-protocol.md` §8.3):** döngü sayısı ve sonuçları, gecikmeli döngü, kesintiler, reconciliation, `Unknown` ve korumasız pozisyon sayıları, Testnet emir yaşam döngüsü sayıları, sıfırlama ve rebaseline, alarm geçmişi, servis belleği.
- **Bitiş:** başlangıçtan ≥ 30 takvim günü sonra (en erken ~2026-12-01); kullanıcının `m6-update.ps1 -ChecksOnly` ve paper raporu çıktılarıyla `m10-acceptance-report.md` ekine işlenir. Prova sırasında en az bir Testnet sıfırlaması ve rebaseline beklenir (ADR-0021).

## 6. Kapı C koşuları (kullanıcı)
| # | Adım |
|---|---|
| C0 | `m6-update.ps1` (migration gerekirse dahil, Trading.Host yeni kodla, `-ChecksOnly`) |
| C1 | `m6-testnet-activate.ps1 -Strategy ema-trend -Reason "..."` ve `-Strategy supertrend` (M6 C4) |
| C2 | İlk Testnet döngülerinden sonra `m6-update.ps1 -ChecksOnly`: korumasız pozisyon 0, takılı `Unknown` 0, reconciliation temiz. **Prova başlar** |
| C3 | `m10-alarm-drill.ps1`; gerekirse `m5-henv.ps1` ve tatbikatın tekrarı |
| C4 | Runbook provası: `m5-backup.ps1` + `m5-restore-test.ps1`; 2026-10 faturası için `m4-ai.ps1 reconcile`; dashboard'un tarayıcı kontrolü (gerçek Testnet emrinden zincir, Kohort 0 ilk emrini verdiğinde) |
| C5 | Kabul: `m9-acceptance-report.md`, `m10-acceptance-report.md`, `m6-acceptance-report.md` C4 eki, roadmap, requirements, CHANGELOG |

## 7. Kabul (roadmap eşlemesi)
| Kriter | Kanıt |
|---|---|
| M9: emirden veri setine zincir | §2.1 testleri (paper dolumu, Testnet emri, allocator düğümü); C4 tarayıcı kontrolü |
| M9: DecisionTrace, degraded, LLM acil durdurma görünür | §2.2 testleri (sentetik izler) |
| M9: görev sınıfı bazında maliyet | §2.3 testleri (sentetik ledger, bütçe yüzdesi) |
| M10: alarmlar test edildi | §3.3 testleri; C3 tatbikat raporu |
| M10: runbook (anahtar iptali, kapanma sonrası devam, Docker bellek) | `docs/runbook.md`; C4 provası |
| M10: 30 günlük Testnet provası | C2 başlangıcı; gözlem arka planda (U-M9-5, kabul edilen sapma) |

## 8. Korkuluklar (T17; Kapı A'da uygulanır)
- `.claude/settings.json`: `Bash(*-File*m10-*)` ve `PowerShell(*-File*m10-*)` deny kuralları.
- `CLAUDE.md`: M9/M10 maddesi (`m10-*.ps1`, `/alarms` ve `/provenance/order/*` gerçek ortamda Claude Code tarafından çalıştırılmaz ve çağrılmaz).

## 9. Kapı B uygulama notları (2026-11-01)
- **Kod:**
  - Execution: `Promotion.MinimumDays = 10`, 7 günlük pencere parametre; `Alarms.cs` (`AiBudget`, `AiSpend`, `AlarmThresholds`, `AlarmEvaluator`); `DashboardQueries`'te ortak zincir kurucusu (`ChainAsync`), `OrderProvenanceAsync`, allocator düğümü, yeni maliyet ve iz sorguları, `AlarmsAsync`.
  - Trading.Host: `GET /provenance/order/{id}` (yalnızca `clientOrderId` karakterleri, ≤ 36), `GET /alarms`; `AiBudget` `ai.v1.json`'dan (`TradingHost.Budget`). Dashboard: alarm şeridi, alarm tablosu, bütçe çubuğu, görev sınıfı/ay/iş tabloları, DecisionTrace sütunları, kill switch ayrıntıları, Testnet emrinden zincir, allocator adımı, D30 metinleri sunucudan.
  - Script'ler: `m10-alarm-drill.ps1` (yeni); `m5-henv.ps1` bitişte `henv_run_recorded` yazar; `m5-paper-start.ps1` Testnet anahtarı kayıtlıysa Testnet'i açık tutar.
- **Sapma ve kararlar (T):**
  - Migration yok: gereken bütün `SELECT` yetkileri mevcuttu.
  - Zincir, borsa emrinin `decision_bar`'ında biten pencereyle (o mum ve önceki iki mum) sabitlenir; paper dolumunda pencere dolum mumunda biter (M9a ile aynı).
  - `unprotected-position` ve `testnet-reset-suspected` alarmlarının kaynağı, son döngülerin `degraded` listesindeki kesintisiz seridir (`unprotected-stop`, `testnet-reset-suspected`); rebaseline ya da stop yerleşince bir sonraki döngüde kaybolur.
  - `/ai/traces` ham `validator_results` ve `safety_violations` metnini artık dönmez; validator adları, ret sayısı ve ihlal sayısı döner (model çıktısından türeyen metin dashboard'a taşınmaz).
  - **Bulunan operasyon tuzağı:** `m5-paper-start.ps1` Trading.Host'u `PI_TRADING_TESTNET` ayarlamadan yeniden oluşturuyordu (compose varsayılanı `disabled`). Makine yeniden başlatıldıktan sonra bu script kullanılsaydı 30 günlük prova sessizce Testnet'siz sürerdi. Düzeltildi; mimari test her yeniden oluşturma script'inin Testnet kararını açıkça verdiğini sabitler.
- **Testler:** `AlarmTests` (sınır değerleri, `ops_api` okuması, rota), `DashboardTests` (yeni rotalar, sentetik iz/maliyet/bütçe, allocator düğümü, model metni sızmaz), `TestnetTradingTests` (borsa emrinden ve Testnet dolumundan zincir), `PromotionTests` (10 gün, pencere sınırı, catch-up mumları), `RepositoryRuleTests` (`m10-*`), `EnvironmentConfigurationTests` (Testnet kararı).
