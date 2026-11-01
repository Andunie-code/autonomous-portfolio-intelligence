# Belge Değişiklik Kaydı

M0 ile birlikte git'e geçildi; bundan sonra ayrıntılı geçmiş git'te, karar düzeyindeki değişiklikler burada.

## 2026-11-01 — Tam H-ENV koşusu ve Kohort 1'in Testnet terfisi (U)
- **H-ENV (aylık kanıt):** `m5-henv-20261101T002326Z.txt`, SHA-256 `a71b3da30fad777a3127069985838ac09c84b60a235fdbffe93086b2f16ebf58`.
  - `holdout-evaluator` durdurulduktan sonra düzeltilmiş script'le koşuldu: **78 satır, 0 hata**; zorunlu kimliklerin hepsi var; `henv_run_recorded` PASS.
  - Koşu `-Cohort`'suz yapıldığı için H22 yok; M5'teki 79 satırla fark budur.
  - 58 satırlık eksik koşu (`018cc233…4fef`) kayıtta kalır ama kanıt sayılmaz.
- **Kohort 1 (U):** `h08`, `h04` ve `h07` `TestnetActive` (`days=10,24/10`, `bars=61/60`, `criticalErrors7d=0`). D35'teki "yalnızca Kohort 0" kapsamı kullanıcı kararıyla genişledi. Lifecycle `TestnetActive=5`; Testnet sermayesi kohort başına 5.000 USDT (A-4 v1.1).
- **Belgeler:** `m10-acceptance-report.md` §3, §6 ve §7 eki; `m6-acceptance-report.md` §7; requirements D35 notu; roadmap M10 durumu. Kod değişikliği yok.

## 2026-11-01 — M9 ve M10 tamamlandı (kullanıcı kabulü; M0–M10 kapandı)
- **Kapı C (kullanıcı koşuları):**
  - C0: `m6-update.ps1`.
  - C1: Kohort 0 (`ema-trend`, `supertrend`) `TestnetActive` (M6 C4). D30 Kohort 0 için 11,38/10 gün, 68/60 mum, kritik hata 0.
  - C2: açık Testnet run 1, bilinmeyen emir 0, korumasız pozisyon 0; Testnet reconciliation henüz `none`. 30 günlük prova başladı.
  - C3: alarm tatbikatı PASS (`da035d68…0745`; ikinci koşu `8fbb3c0c…ab4f`), kritik 0, uyarı 0.
  - Tarayıcı kontrolü yapıldı.
- **H-ENV koşusu eksik:** `m5-henv-20261101T000640Z.txt` (`018cc233…4fef`) 58/58 PASS. Ancak açık kalan evaluator container'ı yüzünden üç probe çalışmadı (M5'te 79 satır). Bu koşu aylık kanıt sayılmaz ve yeniden koşulur.
- **Düzeltme:** `m5-henv.ps1` artık çıkış kodu sıfır olmayan probe'u ve satırı çıkmayan zorunlu kimliği FAIL yazıyor (`EnvironmentConfigurationTests`). Runbook §5.1'e not eklendi.
- **C4 yedeği:** kullanıcı kararıyla M5 kanıtı (33/33 tablo eşleşti) kabul edildi; bu Kapı C'de yeni yedek alınmadı.
- **Kabul edilen sapmalar ve açık konular:**
  - 30 günlük Testnet provası arka planda sürer (D35).
  - Gerçek Testnet emrinden zincir, ilk emirden sonra kontrol edilecek.
  - Kill switch tatbikatında Testnet pozisyonu yoktu. Stop'ların korunmasının gerçek ortam kanıtı M6 C2 tatbikatıdır.
  - 2026-10 fatura mutabakatı açık.
- **Belgeler:**
  - Yeni: [`m9-acceptance-report.md`](m9-acceptance-report.md), [`m10-acceptance-report.md`](m10-acceptance-report.md).
  - `m6-acceptance-report.md` §7 (C4 eki).
  - roadmap M9 ✅ ve M10 ✅ (rev. 5.12), requirements rev. 5.12.
  - ADR-0024 ve protokol durum satırları.
- **`verify.ps1`:** yeşil, **795 test** (Architecture 129: H-ENV eksik koşu testi).

## 2026-11-01 — M9–M10 Kapı B: D30 kodu (10 gün), dashboard tamamlaması, alarmlar, alarm tatbikatı ve runbook
- **D30 (`m6-protocol.md` v1.2 §14):** `Promotion.MinimumDays = 10`; 7 günlük kritik hata penceresi SQL'de `ErrorWindow`'dan gelir; dashboard D30 metinleri sunucudaki eşiklerden üretilir. Yeni testler: 9,99/10 gün sınırı, 7 günlük pencerenin dışlayıcı sınırı, tek catch-up'ta işlenen mumların sayılması.
- **M9 (`m9-m10-protocol.md` §2):**
  - Zincir Testnet borsa emrinden açılır: `GET /provenance/order/{id}` (`clientOrderId` ya da borsa emir no); emir, durum geçmişi, trade'ler, dolumlar, karar mumuyla sabitlenmiş L2/L1, strateji, manifesto, deneyler, veri seti. Testnet dolumu borsa emrini gösterir; `/fills` ve zincir venue döner; Testnet emir tablosu tıklanabilir.
  - Allocator düğümü: `AllocatorAdjusted` (T11 → uygulanan ağırlık, çarpan), `AllocatorIgnored`, veto, öneri kaydı ve DecisionTrace bağlantısı; `/decisions` allocator olaylarını içerir.
  - DecisionTrace: istenen ve gerçek model, `degraded`, validator adları ve ret sayısı, güvenlik ihlali sayısı, gecikme, token, tahmini ve gerçek maliyet. Ham validator ve ihlal metni artık dönmez.
  - Maliyet: ay × görev sınıfı × iş türü, ay ara toplamı, görev sınıfı toplamı, bu ayın işleri ve iş tavanları, aylık tavan ve kullanım oranı (ledger'ın cap hesabıyla aynı: kesinleşen ya da açık rezervasyon); bütçe çubuğu. Kill switch kartları aktör, neden ve zamanı gösterir.
- **M10 (ADR-0024):** `GET /alarms` ve `AlarmEvaluator` (A1–A7, B-1…B-5); her sekmede alarm şeridi ve "Sistem ve AI"de alarm tablosu; `scripts/m10-alarm-drill.ps1` (yalnızca kullanıcı); `m5-henv.ps1` bitişte ops'a `henv_run_recorded` yazar; [`runbook.md`](runbook.md) (alarm → eylem, bant dışı anahtar iptali, kapanma sonrası devam, `.wslconfig`, aylık takvim, Testnet sıfırlaması, prova kontrol listesi).
- **Bulunan ve düzeltilen tuzak:** `m5-paper-start.ps1` Trading.Host'u `PI_TRADING_TESTNET` ayarlamadan yeniden oluşturuyordu; yeniden başlatma sonrası Testnet sessizce kapanırdı. Artık Testnet anahtarı kayıtlıysa açık kalır (mimari testle sabit).
- **Migration yok;** gereken `SELECT` yetkileri mevcuttu. Yeni POST yok; `research_app` izinleri değişmedi.
- **Belgeler:** ADR-0020 uç nokta tablosu, ADR-0024 uygulama durumu, `m9-m10-protocol.md` §9, `testing-strategy.md` §5.1, roadmap M9/M10 durumu.
- **`verify.ps1`:** yeşil, **794 test** (önceki 773; Ai 171, Architecture 128, Backtest 136, HoldoutEvaluator 46, Infrastructure 27, MarketData 97, SourceGateway 69, Trading 120). API maliyeti 0.

## 2026-11-01 — M9–M10 Kapı A (kullanıcı onayı): `m9-m10-protocol.md` v1, ADR-0024, D30 rev., D35
- **D30 rev. (U, 2026-10-30):** `PaperTrading → TestnetActive` asgari süresi 14'ten **10 takvim gününe** indi; ≥ 60 işlenmiş 4h mum ve son 7 günde sıfır kritik hata değişmedi. Gerekçe: 60 × 4h = 10 gün. `m6-protocol.md` **v1.2** (§14; §6.2'deki lifecycle aktörü kodla uyumlu olarak `user`), `requirements.md` D30, `architecture.md` §5 (eski "TBD" satırı), ADR-0021 notu, `m6-acceptance-report.md` §5.
- **Yeni ön kayıt:** [`m9-m10-protocol.md`](m9-m10-protocol.md) v1 ve [ADR-0024](adr/0024-alarms-and-dashboard-completion.md); karar **D35** (U, 2026-11-01): M6 C4'te yalnızca Kohort 0; alarmlar için yeni dış kanal yok; M10, 30 günlük prova başlatılınca kabul edilen sapmayla kapanır.
- **M9 eksikleri (keşif, salt okuma):** Testnet borsa emrinden zincir yok; allocator çarpanı zincirde yok; `/ai/traces` `requested_model` dönmüyor, token ve tahmini maliyet görünmüyor; görev sınıfı toplamı ve bütçe çubuğu yok.
- **M10 tasarımı:** hesaplanan alarmlar (A1–A7, eşikler B-1…B-5), `GET /alarms`, `m5-henv.ps1`'in ops'a `henv_run_recorded` koşu kaydı, `scripts/m10-alarm-drill.ps1`, `docs/runbook.md`.
- **Belge düzeltmesi:** AI kill switch'in kapatılması yalnızca CLI'dadır (`architecture.md` §3.4, `risk-model.md` §3.2'deki "Ops API veya CLI" düzeltildi). `architecture.md` §11 alarm satırı ADR-0024'e bağlandı.
- **T17:** `.claude/settings.json`'a `*-File*m10-*` deny kuralları (Bash, PowerShell); `CLAUDE.md` M9–M10 maddesi. Testleri Kapı B'de (`RepositoryRuleTests`).
- requirements rev. 5.11, roadmap rev. 5.11 (M9/M10 kapıları). Kod değişikliği yok.

## 2026-10-31 — M8 tamamlandı (kullanıcı kabulü, kabul edilen sapma): allocator gölge modda
- **Kapı C (kullanıcı koşuları):**
  - C0: migration 0011 (`already applied 11`), Gemini runtime anahtarı, Trading.Host yeniden derleme. İlk döngü `ok`, kill switch kapalı; `PaperTrading=5`; Testnet açık, bilinmeyen emir ve korumasız pozisyon 0; son 7 günde kritik hata 0.
  - C1: `alloc-spike` üç senaryoda `PASS` (clamp 0, veto 0, güvenlik olayı 0), 0,005031 USD.
  - C2: pilot protokolü kaydedildi (`709c26e99f63…`, 14 gün, 1,00 USD); `Disabled → Shadow`.
- **Kapanış (U-M8-4, U 2026-10-31):** C3 (pilot raporu, süre `N`, yeterlilik protokolü) ve yeterlilik penceresi arka planda süren operasyonel gözlemdir; sonuçlar kabul raporuna ek olarak işlenir. `Passed` çıkmazsa T11 kalıcıdır (D20). Gerçek emirler T11 ile verilmeye devam eder.
- **Script'ler:** `m5-paper-start.ps1` ve `m6-update.ps1` Trading.Host'u `--force-recreate` ile başlatır; yeni imaj ve değişen secret dosyaları her başlatmada yeniden yüklenir (`EnvironmentConfigurationTests` ile sabitlendi).
- **Belgeler:** [`m8-acceptance-report.md`](m8-acceptance-report.md); 2026-10 mutabakatı yenilendi (ledger 0,749711 USD; AllocationProposal 0,005031 USD; pilot işi henüz ledger'da yok); roadmap M8 ✅ (rev. 5.10); requirements rev. 5.10; `m8-protocol.md` ve ADR-0023 durum notları.
- **`verify.ps1`:** yeşil, **773 test** (önceki 770; Ai 171, Architecture 125, Backtest 136, HoldoutEvaluator 46, Infrastructure 27, MarketData 97, SourceGateway 69, Trading 102).

## 2026-10-30 — M8 C0 düzeltmesi: Dockerfile'larda eksik proje kopyaları
- **Hata:** `m8-update.ps1`'in Trading.Host derlemesi (C0, adım 3/5) başarısız oldu. Dockerfile, M8'de eklenen `Ai`, `Ai.Gemini` ve `Allocator` projelerini kopyalamıyordu. Migration 0011 ve runtime anahtarı bu hatadan önce uygulanmıştı.
- **Düzeltme:** Trading.Host Dockerfile'ına üç `COPY` satırı eklendi. Yeni test, Research.Host Dockerfile'ında M7'den beri eksik olan `SourceGateway` kopyasını da buldu; o da eklendi. İki imajın derleme aşaması yerelde başarıyla derlendi (`docker build --target build`).
- **Kalıcı koruma (`EnvironmentConfigurationTests`):** her Dockerfile, yayımladığı projelerin geçişli tüm `ProjectReference` dizinlerini ve kök props dosyalarını kopyalamalı. Öz-test: Ai satırı çıkarılmış Trading.Host Dockerfile'ı yakalanıyor. Architecture testleri 124/124.

## 2026-10-30 — M8 Kapı B belgeleri onaylandı (U): `m8-protocol.md` v1.1 ve pilot protokolü
- Kullanıcı `m8-protocol.md` v1.1'i (§8 hash'leri ve §11 uygulama sapmaları dahil) ve `m8-alloc-pilot-protocol.md`'yi (14 gün, 1,00 USD, `gemini-3.5-flash-lite`, seed 83000) onayladı. Durum satırları güncellendi; ön kayıt kanıtı bu commit'tir.
- Sıradaki adımlar kullanıcının koşularıdır: C0 (`m8-update.ps1 -SetKey`), C1 (`alloc-spike --allow-network`), C2 (`alloc-register` ve `Disabled → Shadow`).

## 2026-10-29 — M8 Kapı B: Allocator modülü, iki fazlı adım, gölge motor, mod durum makinesi, migration 0011 (m8-protocol v1.1)
- **Kod (çevrimdışı, API maliyeti 0; ayrıntı `m8-protocol.md` §11, ADR-0023 uygulama notu):**
  - Kernel: `PortfolioEngine.BeginStep`/`CompleteStep` (override yokken davranış ve hash'ler birebir), `StepContext.Allocation`, `allocator-band v1`, L2 olayları `AllocatorAdjusted` ve `AllocatorIgnored`; port `IAllocationAdvisor` ve DTO'lar.
  - Yeni `Allocator` modülü (Kernel + Ai): opak referanslı girdi paketi, `allocation-prompt v1` ve `allocation-proposal v1`, çıktı doğrulayıcısı (açık pozisyona ya da bekleyen çıkışa referans = `exit-interference` güvenlik olayı), deterministik clamp, `AllocatorGate`, `AllocationAdvisor` (gölge: yeterlilik modu ve protokol bütçesi; aktif: production modu), non-inferiority kuralı ve süre kuralı, protokol belgesi ayrıştırıcısı, gölge değerlendirmesi.
  - Execution: gölge motor (gerçek adımdan sonra; restart'ta saklanan override'larla LLM'siz yeniden kurulur ve durum hash'i doğrulanır), aktif mod (yalnızca `Validated` sleeve'ler; override `StepInputs`'a yazılır), allocator store'u, mod geçişi ve dashboard sorgusu. Allocator yolundaki her hata T11'e düşer; döngü durmaz.
  - Trading.Host: allocator bağlaması (anahtar dosyası yoksa her karar T11), `GET /allocator`, `POST /allocator/mode`, "Allocator" sekmesi.
  - Ai: `JobKind.Allocation` (iş payı 0,10), `QualificationDesign.NonInferiority`; AllocationProposal zaman aşımı 30 sn (A-6).
  - Migration 0011: `trading.allocator_mode_events` (kural trigger'ı), `trading.allocator_proposals`, `trading.allocator_shadow_steps` (append-only); iki `SECURITY DEFINER` kontrol fonksiyonu; `design`, `job_kind` ve `ops_actions` kısıtları; grant'lar (`research_app`'e hiçbir şey).
  - Araçlar ve script'ler: AiCli `alloc-register|alloc-spike|alloc-report|alloc-qualify`; `scripts/m8-update.ps1`, `m8-allocator-mode.ps1`, `m8-allocator.ps1` (yalnızca kullanıcı; T17 deny kuralı); Compose secret'ı `gemini_runtime_api_key` (boş yer tutucu), `m5-paper-start.ps1` ve `m6-update.ps1` yer tutucuyu oluşturur.
- **Mimari kural (ADR-0023):** Execution {Kernel, MarketData} kalır; yalnızca Trading.Host Allocator, Ai ve Ai.Gemini'ye ulaşır; Allocator {Kernel, Ai} ve kaynak taraması temiz (yerleştirilmiş ihlalle öz-test).
- **Testler:** Kernel override değişmezleri I-1…I-4 (rastgele override'larla property testleri), allocator birim testleri (her fallback yolu, kalibrasyon kolları: oracle, attacker, fabricator), protokol belgesinin koda bağlanması, non-inferiority kuralı, DB ve uygulama mod kuralları, roller, Ops API, gölge modda gerçek adımların birebir aynı kalması ve LLM'siz yeniden kurulum, aktif modda yalnızca `Validated` run'ın ayarlanması ve replay.
- **Regresyon kilitleri (Claude Code, `R`):** M2'nin dört hash'i birebir (`parity-m3`); Kohort 0 ve Kohort 1 L1/L2 hash'leri ve Kohort 1 rapor özeti M5/M5b ile aynı (kuru `parity-m5`).
- **`verify.ps1` yeşil: 770 test** (önceki 715; Ai 171, Architecture 122, Backtest 136, HoldoutEvaluator 46, Infrastructure 27, MarketData 97, SourceGateway 69, Trading 102).
- **Onay bekleyen (U; 2026-10-30'da onaylandı):** `m8-protocol.md` v1.1 ve `m8-alloc-pilot-protocol.md` (14 gün, 1,00 USD, seed 83000).

## 2026-10-29 — M8 Kapı A (kullanıcı onayı): `m8-protocol.md` v1, ADR-0023, D34
- **Kullanıcı kararları (U, 2026-10-29; D34):**
  - gölge öneriler tüm paper stratejileri için; aktif modda ağırlık yalnızca `Validated` stratejilere;
  - bant `m × w_T11`, m ∈ [0,5; 1,5] ya da veto; bant dışı clamp; onaylı tavanlar ardından değişmeden;
  - model `gemini-3.5-flash-lite`; bütçe pilot 1,00 USD, yeterlilik 3,00 USD;
  - kapanış kabul edilen sapma; yeterlilik penceresi arka planda sürer.
- **Kullanıcı onayı (U, 2026-10-29):** protokol v1, A-1…A-7, ADR-0023 ve D34 onaylandı. Ön kayıt kanıtı bu commit'tir.
- **Belgeler:**
  - `m8-protocol.md` v1: girdi paketi, şema, clamp/red tablosu, fallback nedenleri, mod durum makinesi, gölge motor, non-inferiority karar kuralı, pilot ön kaydı, görünürlük kuralları. A-1…A-7 (§0.2): NI marjı yıllık −%3; drawdown toleransı 0; geçerli öneri ≥ %80; süre kuralı 28–56 gün; ≥ 10 geçerli öneri; timeout 30 sn; pilot 14 gün.
  - ADR-0023: yeni `Allocator` modülü (Kernel + Ai); Trading.Host bağlar, Execution AI'dan bağımsız kalır; iki fazlı adım; gölge motor; sızıntı korumaları.
- **Güncellenen belgeler:** `requirements.md` rev. 5.9 (D34, §8 gölge süresi satırı); `roadmap.md` (M8 kapıları); `architecture.md` §1, §4, §5; `testing-strategy.md` §6.2; `risk-model.md` §4; ADR-0005, ADR-0011 (uygulama durumu düzeltildi) ve ADR-0018 notları.
- **`sources.md`:** S-G1 ve S-G2 yeniden doğrulandı (2026-10-29); fiyatlar değişmedi. SDK sürümü değişmez.
- **T17:** `CLAUDE.md`'ye M8 kuralları; `.claude/settings.json`'a `*-File*m8-*` deny kuralları (`RepositoryRuleTests` genişletildi).
- **Doğrulama:** `verify.ps1` yeşil: **715 test** (önceki 713; Architecture +2).

## 2026-10-28 — M7 tamamlandı (kullanıcı kabulü): FindingExtraction `Failed`, web araştırması kapalı
- **C4 (kullanıcı; protokol `9e8bc4ad…`, commit'li belgeyle aynı):** **`Failed`** (`TaskQualification` 2).
  - 27/30, tek yönlü kesin binom p = 0,1227 ≥ 0,05; geçmek için 28/30 gerekiyordu.
  - Güvenlik ihlali 0; uydurma alıntı 0; maliyet 0,044213 USD.
  - Üç başarısızlık da normal öğelerde `unsupported-number` reddi. Injection 9/9 ve negatif kontrol 6/6 başarılı.
  - Rapor: `m7-fe-qualification-results.md`.
- **Kullanıcı kararı (U):** v2 döngüsü açılmaz. `qualifiedModels: []` kalır; web araştırması kapalıdır (D20).
- **C6:** `m7-web-comparison.md`, kapalılık biçiminde. Web'li kol boş; web'siz kol koşulmadı ve global deneme sayacı değişmedi. Kriter 7, araç ve çevrimdışı testlerle karşılandı (m7-protocol §9).
- **C7:** `m4-ai-reconciliation-2026-10.md` güncellendi.
  - Ledger toplamı 0,744680 USD (190 çağrı); FindingExtraction 0,072868 USD.
  - Fatura ay kapanınca girilir.
- **Kabul edilen sapma:** C2 (model sürümü spike'ı) koşulmadı. Router'ın degraded kontrolünde eşleşmeyen yanıt görülmedi.
- **Güncellenen belgeler:**
  - `m7-acceptance-report.md` (yeni);
  - `roadmap.md` (M7 ✅);
  - `requirements.md` (rev. 5.8, durum satırı ve D14 notu);
  - ADR-0008, ADR-0010, ADR-0022 durum notları.

## 2026-10-28 — M7 Kapı C1 ve C3 tamamlandı; FindingExtraction yeterlilik protokolü (U)
- **C1 (kullanıcı):**
  - arama 4 kayıt döndürdü, düşürülen 0; `arxiv.org/robots.txt` → 200;
  - 4 belge kaydedildi: 3 PDF, 1 özet (`1802.03708v8` için `/pdf/` 404 döndü);
  - provenance raporunda sorunlu 0, önbellek bozulması 0;
  - v1.2 sorgu düzeltmesi gerçek API'de çalıştı.
- **C3 pilotu (kullanıcı; `m7-protocol.md` v1.2, designHash `067181b1…`):**
  - **19/20**; güvenlik ihlali 0; uydurma alıntı 0;
  - 0,028655 USD;
  - tek başarısızlık bir I2 öğesindeki `unsupported-number` reddi, ihlal değil.
  - Rapor: `m7-fe-pilot-results.md`.
- **§7 kuralının uygulanması:**
  - p̂ 0,95 için `n` = 30 (güç 0,812; geçme sınırı 28/30);
  - tahmin 0,086 USD; iş bütçesi 3,00 USD.
- **Yeterlilik protokolü:** `m7-fe-qualification-protocol.md` (U): seed 82000–82029, set `58863a75…`, tek kollu, p0 0,80, α 0,05.
  - Bir test, protokol bloğunun koddaki prompt, şema ve set hash'leriyle ve §7 kuralıyla uyuştuğunu doğrular.

## 2026-10-28 — M7 Kapı C1: arXiv API'nin HTTP 500 yanıtı için sorgu düzeltmesi (m7-protocol v1.2)
- **Gözlem (kullanıcı):** `m7-sources.ps1 search` isteğine API HTTP 500 döndü; CLI hatayı yakalamadan çıkış yaptı.
- **İnceleme:** `q-fin.*` wildcard'ı sunucuya gitmiyordu (`ArxivQuery.Expand` onu dokuz açık alt sınıfa açar). İki şüpheli bulundu:
  - tarih alt sınırı `000001010000` (0000 yılı);
  - boşluğun `%20`, iki noktanın `%3A` olarak gönderilmesi.
- **Düzeltme:**
  - alt sınır `199101010000`;
  - sorgu kılavuz biçiminde kodlanıyor (boşluk `+`, `:` olduğu gibi);
  - `search` hatada istek URL'sini yazıyor ve 2 koduyla çıkıyor;
  - `--print-url` ağsız tanı imkânı veriyor.
- **Değişmeyenler:** U-M7-2 ve istemci tarafındaki `< C` kontrolü, FindingExtraction kuralları ve pilot bloğu.
- **Doğrulama:** Claude Code arXiv'e istek atmadığı için düzeltme gerçek API'de denenmedi; kullanıcının C1 koşusuyla doğrulanacak.

## 2026-10-28 — M7 Kapı B: SourceGateway, Extractor ve doğrulama, Researcher tool'ları, migration 0010 (m7-protocol v1.1)
- **Kod (çevrimdışı, API maliyeti 0; ADR-0022 uygulama notu):**
  - Yeni `SourceGateway` modülü:
    - allowlist (`sources.v1.json`) ve tüm 3xx'in reddi;
    - host başına ≥ 3 sn ve tek bağlantı;
    - robots.txt: arXiv API'si muaf; 404 = kısıt yok; diğer her durum = yasak;
    - 403 sonrası host'a istek yok;
    - boyut ve içerik türü sınırları;
    - repo ve OneDrive dışında önbellek (SHA-256 ve TTL);
    - arXiv sorgu kurucusu ve Atom ayrıştırıcı (DTD kapalı; `published` ve `updated` `< C`);
    - PDF (PdfPig) ve HTML (AngleSharp) gizli metin süzgeçleri;
    - normalizer ve ofset haritası;
    - `SourceDocument` / `Finding` provenance.
  - **Research:**
    - tool'suz `FindingExtractor`;
    - `QuoteVerifier` (normalize metinde tam alt dizgi), sayısal doğrulayıcı, sanitizer;
    - `WebResearchGate` (nitelik yoksa kapalı);
    - Researcher'a `SearchFindings` ve `RequestSourceResearch` (tool sayısı 8);
    - bulgu → hipotez (`Hypothesis.SourceFindingIds`; doğrulanmamış ya da kesim sonrası bulgu reddedilir);
    - `web-compare` rapor aracı.
  - **Yeterlilik:**
    - `fe-synthetic v1` üreticisi (normal, I1–I5 injection, negatif kontrol);
    - `FindingExtractionTask` ve modelsiz kalibrasyon kolları;
    - harness'ta tek kollu kesin binom tasarımı (`QualificationProtocol.Design`, `P0`; HypothesisGeneration yolu değişmedi).
  - **Migration 0010:**
    - `sources` şeması (append-only; arXiv sürümleri için `< C` CHECK; bulgu tutarlılık CHECK'i);
    - `research.hypotheses.source_finding_ids` ve doğrulayan trigger;
    - `ai.qualification_protocols.design` / `p0`;
    - yalnızca `research_app` için `SELECT, INSERT`.
  - **Araçlar:** `SourcesCli`, `AiCli fe-items|fe-calibrate|fe-pilot`, `scripts/m7-sources.ps1` ve `scripts/m7-web-compare.ps1` (yalnızca kullanıcı).
  - Arşiv önbelleğinin repo/OneDrive kuralı Kernel'e taşındı (`LocalCacheRoot`); `ArchiveCacheDirectory` davranışı değişmedi.
- **Paketler:** PdfPig 0.1.16 ve AngleSharp 1.8.4 (`Directory.Packages.props`); net8.0+ için geçişli bağımlılık yok.
- **Protokol v1.1 (`m7-protocol.md` §6, §14):**
  - pilot bloğunun hash'leri: şema `08cbe4ad…`, prompt `8693c280…`, set `54026547…`;
  - kalibrasyon sonucu: dört beklenti de sağlandı;
  - örneklem ızgarası {30, 40, …, 200}; §7'deki betimleyici not düzeltildi (p̂ 0,95 → `n` 30; p̂ 0,90 → `n` 90);
  - iki uygulama sapması:
    - `InvariantGlobalization` nedeniyle NFKC yerine açık uyumluluk tablosu;
    - paragraf ayracı `

` (idempotent normalizer).
- **Kullanıcı kararı (U):** v1.1'deki iki uygulama sapması ve örneklem notunun düzeltilmesi onaylandı.
- **Testler:** yeni proje `SourceGateway.Tests`; Ai, Backtest, Architecture ve Trading testlerine eklemeler.
- `verify.ps1` yeşil: **711 test** (önceki 599; Ai 137, Architecture 115, Backtest 123, HoldoutEvaluator 46, Infrastructure 27, MarketData 97, SourceGateway 68, Trading 98); build 0 uyarı / 0 hata; secret scan geçti.
- **Güncellenen belgeler:** ADR-0022 uygulama notu, `roadmap.md` M7 durumu, `scripts/m4-ai.ps1` yardım metni.

## 2026-10-28 — M7 Kapı A: kaynak koşulları, protokol v1 ve ADR-0022 (kullanıcı onayı)
- **Kullanıcı kararı (U):** `m7-protocol.md` v1, A-1…A-9, §11 (içerik görünürlüğü), §13 (akış sonucu) ve ADR-0022 onaylandı. Ön kayıt kanıtı bu commit'tir.
- **Ön kontrol:** M6 commit'leri zaten `origin/main`'deydi (`dc6b483`, fetch ile doğrulandı). Push yapılmadı.
- **Kullanıcı kararları (U, `m7-protocol.md` §0.1):**
  - **U-M7-2:** yalnızca `published` ve `updated` tarihi `< C` olan arXiv sürümleri kabul edilir.
  - **U-M7-3:** Semantic Scholar M7'de kullanılmaz.
  - **U-M7-4:** FindingExtraction modeli `gemini-3.5-flash-lite`.
  - **U-M7-5:** bütçe pilot 0,50 USD, yeterlilik 3,00 USD.
  - **U-M7-6:** arXiv API'si `robots.txt` yerine API koşullarına tabidir; diğer yollar için `robots.txt` bağlayıcıdır.
- **Kaynak doğrulaması (`sources.md` S-X1…S-X12):**
  - arXiv API koşulları: 3 sn'de bir istek ve tek bağlantı; metadata CC0; içerik yalnızca kişisel ve araştırma kullanımı için.
  - API kılavuzu ve Atom alanları: `updated`, alınan sürümün tarihidir.
  - **`export.arxiv.org/robots.txt` `Disallow: /`** diyor. `arxiv.org/robots.txt` okunamadı (503).
  - Erişim ve toplu veri politikaları, lisanslar, Semantic Scholar limitleri, `developers.binance.com/robots.txt` (404).
  - Paketler: PdfPig 0.1.16, AngleSharp 1.8.4.
  - S-G3 ve S-G5 (grounding) yeniden doğrulandı; T13'ün dayanağı değişmedi.
  - Makale içeriği ve API yanıtı okunmadı.
- **Yeni belgeler:**
  - `m7-protocol.md` v1 (taslak):
    - allowlist ve erişim kuralları;
    - metin çıkarma ve normalizer;
    - `finding-extraction v1` şeması ve kabul hattı;
    - `fe-synthetic v1` seti (normal, I1–I5 injection, negatif kontrol);
    - tek kollu karar kuralı (H0: p ≤ 0,80);
    - pilot ön kaydı, örneklem ve bütçe kuralı;
    - Researcher tool'ları ve bulgu → hipotez yolu;
    - Kapı C adımları.
  - ADR-0022 (önerildi).
- **TBD kapandı:** PDF metin çıkarma kütüphanesi (requirements §8).
- **Korkuluklar (T17 eki):**
  - `.claude/settings.json` deny kuralları: `export.arxiv.org`, `arxiv.org/pdf`, `m7-*.ps1`;
  - `CLAUDE.md`'ye M7 maddesi eklendi;
  - `RepositoryRuleTests` Kapı B'de genişletilir.
- **Güncellenen belgeler:** `requirements.md` (rev. 5.7; D14 notu, §8), `architecture.md` §8, `roadmap.md` M7, `testing-strategy.md` §6.2 ve §6.4, `risk-model.md`, ADR-0008 uygulama notu.
- **Kod değişikliği yok.**

## 2026-10-28 — M6 tamamlandı (kullanıcı kabulü)
- **Kullanıcı kararı (U):** M6, C1–C3 kanıtlarıyla kapatıldı.
  - **Kabul edilen sapma:** gerçek `PaperTrading → TestnetActive` terfisi (C4), D30 dolunca (en erken Kohort 0 için 2026-11-03, Kohort 1 için 2026-11-04) operasyonel adım olarak yapılır. M5'teki paper gözlemiyle aynı biçimde ele alındı.
- **C3 (`m6-update.ps1`, kullanıcı):**
  - migration 0009 uygulandı; Trading.Host M6 koduyla, Testnet açık olarak yeniden başladı; ilk döngü `ok`;
  - Testnet: açık run 0, `Unknown` 0, korumasız pozisyon 0; borsaya emir gitmedi.
  - D30: Kohort 0 7,33/14 gün, 43/60 mum; Kohort 1 6,17/14 gün, 36/60 mum; kritik hata 0.
- **Paper raporu** (SHA-256 `82fa39d4…9224`):
  - 56 döngü; gecikmeli döngü 0; reconciliation 75/0; duplicate 0; negatif nakit 0; canlı mum anomalisi 0;
  - üç kesinti; kapanıştan 5 dakikadan geç işlenen 71 mum (kesinti sonrası catch-up);
  - bellek Trading.Host 54,92 MiB, PostgreSQL 62,18 MiB.
- **Script düzeltmesi:** `m6-update.ps1`'in lifecycle sayımı. Windows PowerShell 5.1 JSON dizisini tek nesne döndürdüğünde `@()` diziyi bir kez daha sarmalıyordu ve tüm durumlar tek grupta toplanıyordu. Dizi artık pipeline ile açılıyor.
- **Güncellenen belgeler:** `m6-acceptance-report.md` (C3, C4 planı, kabul tablosu), `roadmap.md` (M6 ✅), `requirements.md` durum satırı, ADR-0021 durumu.

## 2026-10-28 — M6: Testnet sermayesi 5.000 USDT (m6-protocol v1.1, A-4; U)
- **Kullanıcı kararı (U):** seçenek A. Testnet run sermayesi kohort başına 5.000 USDT nominal. Paper run'ları 10.000 ile sürer.
- **Gerekçe:** Testnet'teki USDT bakiyesi yaklaşık 10.000 (kullanıcı beyanı; aylık sıfırlamada geri döner). %80 maruziyet sınırıyla iki kohort artık en fazla yaklaşık 8.000 USDT tutar.
- **Kod:**
  - `PaperRunConfig.ForTestnet` (sleeve'ler aynı, sermaye ölçekli);
  - tatbikatın sermaye kontrolü `kohort × 5.000 × 0,80` (`TestnetDrillCommand.RequiredCapital`); script'teki 20.000 varsayılanı kaldırıldı;
  - dashboard özet toplamları yalnızca paper run'larını içerir; Testnet run kartında rozet var.
- **Testler:** Testnet run'ının sleeve'leri paperla aynı, sermayesi 5.000; paper 10.000; özet toplamı yalnızca paper; tatbikat eşiği 2 kohort için 8.000.
- `verify.ps1` yeşil: **599 test** (Trading 97); build 0 uyarı / 0 hata; secret scan geçti.
- **Belgeler:**
  - `m6-protocol.md` v1.1 (§0.2 A-4, §4.1, yeni §13);
  - `m6-acceptance-report.md` §3;
  - ADR-0021 uygulama notu.

## 2026-10-28 — M6 Kapı C: C1 ve C2 tamamlandı (tatbikat PASS)
- **C1 (kullanıcı):** Testnet anahtarı kaydedildi.
- **C2 (kullanıcı):** tatbikat PASS. Rapor SHA-256 `0d82ad11…2ccc`.
  - 24 emir; 23 tam yaşam döngüsü; duplicate 0.
  - `Unknown` 1/1 çözüldü (`NeverAccepted`, gerçek borsada).
  - `STOP_LOSS` 20/20 sembolde destekleniyor; `canTrade=true`.
  - Saat ofseti 433 ms; gecikme medyanı 264 ms, p95 503 ms.
- **Roadmap M6 kriteri "≥ 20 emrin tam yaşam döngüsü" karşılandı.**
- **Bulgu:** `capitalSufficient=false`. Serbest USDT, iki kohort × 10.000 nominal için yetmiyor; A-4 kararı bekliyor (`m6-acceptance-report.md` §3).
- **Yeni belge:** `docs/m6-acceptance-report.md` (Kapı C sürüyor).
- **Güncellenen belge:** `sources.md` M6 doğrulanamayanlar listesi (gözlenenler işaretlendi; stop tetik fiyatı ve bakiye kilidi hâlâ açık).

## 2026-10-28 — M6 Kapı C: tatbikat script düzeltmeleri (kullanıcının ilk koşusu)
- **İlk `m6-testnet-drill.ps1` koşusu (kullanıcı) "Erişim engellendi" ile durdu.** İki neden vardı:
  1. `m6-testnet-key.ps1`, secret dosyalarının miras alınan izinlerini kaldırmıştı (`icacls /inheritance:r`). Docker Desktop bu dosyaları mount edemedi.
  2. Tatbikat, M5b'den kalan `trading-host` imajıyla koştu; bu imajda `testnet-drill` komutu yok.
- **Düzeltmeler:**
  - Secret dosyaları diğer `secrets/` dosyalarıyla aynı, miras alınan izinlerle yazılıyor.
  - Yeni `Reset-SecretAcl` yardımcısı (`icacls /reset`) mevcut dosyaları içeriğe dokunmadan düzeltir; anahtarın yeniden girilmesi gerekmez. Key, drill ve update script'lerinin üçü de bu yardımcıyı çağırıyor.
  - `m6-testnet-drill.ps1` artık commit'lenmiş kodla `docker compose build trading-host` yapıyor; `src/` ya da `scripts/` temiz değilse durur.
- **İkinci koşu (kullanıcı):** imaj derlendi, ancak `docker compose run` "Address already in use" hatasıyla durdu. Neden: `trading-host`'un `ops` ağındaki statik IP'si (172.30.1.20, `pg_hba`) çalışan container'da kullanılıyordu.
- **Düzeltme:**
  - Tatbikat, çalışan Trading.Host'u `stop` ile duraklatır ve `finally` içinde `start` ile aynı container'ı (aynı imaj ve ayarlar, canlı veri dahil) geri başlatır.
  - `up -d` kullanılmaz: yeni imaj ancak `m6-update.ps1` ile devreye girer.
  - Durma süresi kesinti olarak kaydedilir; kaçırılan mum olursa açılışta sırayla işlenir.
- Ağa istek atılmadı; Testnet'e emir gönderilmedi.

## 2026-10-27 — M6 Kapı B: çevrimdışı kod ve testler (`verify.ps1` yeşil, 598 test)
- **Kernel (ADR-0021 §3):**
  - `PortfolioEngine`'e harici dolum modu eklendi.
  - Simüle mod değişmedi: M2 hash'leri, motor regresyonu ve Kohort 0/1 parity testleri değişmeden geçti (Backtest 119/119).
- **Execution:**
  - `IExchangeAdapter` ve `BinanceTestnetAdapter` (REST, HMAC-SHA256).
  - `TestnetGuardHandler`: yalnızca `testnet.binance.vision`, yol/metot/sembol allowlist'i, 3xx reddi, 418 ve 429.
  - Yerel filtreler; deterministik `clientOrderId` (`pi-<p>-<h>`).
  - Outbox (`ExchangeStore`):
    - `Unknown` emir `origClientOrderId` ile çözülür;
    - `NeverAccepted` için şart: `timestamp + recvWindow + 60 sn` sonrasında iki ardışık `-2013`.
  - Koruyucu stop:
    - giriş dolumundan hemen sonra `STOP_LOSS`, sembolde yoksa bantlı `STOP_LOSS_LIMIT`;
    - yükseltmede cancel-replace;
    - korumasız pozisyonda alarm ve giriş blokajı; güvenlik ağı çıkışı.
  - Çıkışta önce stop iptal edilir, sonra `MARKET` satılır.
  - Kill switch yalnızca giriş emirlerini iptal eder ve anında bir bakım geçişi tetikler.
  - Testnet reconciliation (emirler ve bakiye tabanı); sıfırlama tespiti ve rebaseline.
- **Testnet run'ı:** paper run'ı paralel sürer (U-M6-4). `TestnetActive` üye paper'da aktif kalır.
- **Migration 0009:**
  - outbox tabloları (append-only);
  - "intent başına tek etkin emir" ve durum geçişi trigger'ları;
  - `paper_runs.venue`;
  - V10: `PaperTrading → TestnetActive` yalnızca `ops_api` ile, `d30` kanıtı ve `confirm = true` ile yazılabilir.
- **Ops API ve dashboard:**
  - `GET /testnet`, `GET /promotion`;
  - `POST /strategies/{name}/testnet-activate` (`confirm`; 400 ve 409 yanıtları);
  - `POST /testnet/rebaseline`;
  - "Testnet" sekmesi ve özette D30 ilerleme çubukları.
- **D30 hesaplayıcısı (U-M6-3):** takvim günü, catch-up dahil mum sayısı, son 7 günde kritik hata, kesinti oranı.
- **Trading.Host:**
  - `Trading:Testnet` ayarı; Docker secret'ları `testnet_api_key` ve `testnet_api_secret` (yalnızca trading-host);
  - `testnet-drill` alt komutu;
  - dosya secret'larının literal maskelenmesi; `signature=` ve `X-MBX-APIKEY` desenleri.
- **Script'ler (kullanıcı):**
  - yeni: `m6-testnet-key.ps1`, `m6-testnet-drill.ps1`, `m6-testnet-activate.ps1` (`-Rebaseline` dahil), `m6-update.ps1` (`-ChecksOnly`);
  - `m5-paper-start.ps1` artık boş Testnet secret yer tutucularını oluşturuyor.
- **Testler (m6-protocol §9):**
  - guard, HMAC (Binance belge vektörü) ve adapter hata semantiği;
  - filtre property testleri;
  - fault injection (zaman aşımı, 5xx, -1007, bağlantı kopması, gecikmeli görünürlük, ulaşmayan emir, -1021, 429, 418, cancel-replace -2021);
  - restart; kill switch; çıkış yarışı;
  - reconciliation ve sıfırlama/rebaseline;
  - V10/D30;
  - kernel eşdeğerliği;
  - uçtan uca Testnet akışı (duplicate emir 0, her holding'de stop, kısmi dolum, `STOP_LOSS_LIMIT` yedeği, korumasız stop akışı);
  - tatbikat; Trading testleri için ağa çıkmama taraması;
  - Testnet izolasyonu (mimari) ve M6 deny kuralları.
- **Kapı B'de bulunan ve düzeltilen hatalar:**
  - Harici modda stop dolumları girişlerden önce uygulanıyordu; aynı açılışta giren pozisyonun stop dolumu kayboluyordu. Sıra artık simüle modla aynı.
  - Tek dolumlu girişte decimal yuvarlaması fiyatı değiştiriyordu.
  - Worker, bakım sinyalini kaçırabiliyordu.
- **`verify.ps1`:** test projeleri sırayla koşulur; sayılar TRX'ten toplanır. Paralel Testcontainers örnekleri 16 GB makinede belleği tüketiyordu (ilk koşu bu yüzden durdu).
- **Sonuç:** `verify.ps1` yeşil. Build 0 uyarı / 0 hata; secret scan geçti. Test sayıları:

  | Proje | Test |
  |---|---|
  | Ai | 111 |
  | Architecture | 102 |
  | Backtest | 119 |
  | HoldoutEvaluator | 46 |
  | Infrastructure | 27 |
  | MarketData | 97 |
  | Trading | 96 |
  | **Toplam** | **598** |
- Evaluator ve `*Holdout*` dosyaları değişmedi. Gerçek Testnet'e istek atılmadı.

## 2026-10-26 — M6 Kapı A (kullanıcı onayı): Testnet ön kaydı, ADR-0021, D33
- **Kullanıcı kararları (U):**
  - M6 başlatıldı; kapılar A, B ve C (`m6-protocol.md` U-M6-1).
  - **D33:** B6 kapandı. Testnet anahtarında IP kısıtı yok. Yerine kodda yalnızca `testnet.binance.vision` host allowlist'i, 3xx reddi, secret'ların yalnızca Trading.Host'ta tutulması ve işlem izni geçerli. Karar yalnızca Testnet için.
  - **D30 sayımı:** takvim günü ve catch-up dahil işlenmiş 4h mumlar; kesinti oranı ayrıca gösterilir.
  - Strateji `TestnetActive` olunca paper run'ı paralel sürer.
  - **Emir tipleri:** koruyucu stop `STOP_LOSS` (sembolde yoksa bantlı `STOP_LOSS_LIMIT`); giriş bantlı `LIMIT IOC`; çıkış ve kapatma `MARKET`.
  - İmzalama HMAC-SHA256.
- **Belge doğrulaması:** `sources.md` S-B11…S-B24. Yalnızca `developers.binance.com` belgeleri okundu; Testnet API'sine istek atılmadı. Öne çıkanlar:
  - yeni anahtar varsayılan olarak işlem yapamaz;
  - 5xx ve `-1007` yanıtlarında yürütme durumu bilinmez;
  - `recvWindow`, Matching Engine'den hemen önce yeniden kontrol edilir;
  - Testnet yaklaşık ayda bir, önceden bildirilmeden sıfırlanır ve yalnızca `/api` uç noktaları vardır;
  - `STOP_LOSS` tetiklenince market emri olur;
  - Spot REST'te `newClientOrderId` için biçim kuralı yok (FIX kümesi kullanılır).
- **Yeni belgeler:**
  - `docs/m6-protocol.md` v1 (ön kayıt; §0.2 A-1…A-6 dahil kullanıcı onaylı);
  - `docs/adr/0021-testnet-execution-adapter.md`.
- **Güncellenen belgeler:** `requirements.md` (D33, D30 sayımı, §4, B6), `risk-model.md`, `architecture.md` §5 ve §10, `roadmap.md` (M6 kapıları), `testing-strategy.md` §5, ADR-0012/0013/0014 notları, `sources.md`.
- **M5b arka plan gözlemi:** kullanıcının 2026-10-26 raporu (SHA-256 `9869e507…f9d6`) `m5-acceptance-report.md` §6'ya yalnızca operasyonel alanlarla işlendi:
  - 40 döngü, gecikmeli döngü 0;
  - reconciliation 43/0, duplicate dolum 0;
  - iki kesinti;
  - Kohort 1'in ilk canlı giriş sinyali (sembol ve fiyat yok; araştırma girdisi değildir).
- **Korkuluklar (T17):**
  - `.claude/settings.json`'a `testnet.binance.vision` (WebFetch, curl, wget, PowerShell) ve `*-File*m6-*` deny kuralları eklendi;
  - `CLAUDE.md`'ye M6 Testnet bölümü eklendi;
  - testleri Kapı B'de yazılacak (`RepositoryRuleTests`).
- Kod değişikliği yok.

## 2026-10-21 — M5b tamamlandı (kullanıcı kabulü)
- **Kullanıcı kararı (U):** M5b (Kohort 1 operasyonel paper, M9a dashboard ve sinyal radarı) tamamlandı.
- **Son kontroller (kullanıcı):**
  - `-ChecksOnly`: `paperTrading=3/3` ve `lastCycleDegraded=stale-data`. Döngü, yeniden başlatmadaki açılış döngüsüdür (19:14:07 UTC, 311 bar, ısınma mumları). `strategy-hash-mismatch` yok.
  - Rapor SHA-256 `1e7e7fbf…be22`.
- **Dashboard (kullanıcının tarayıcı kontrolü):**
  - nakit düzeltmesi doğrulandı (20.000,00); Run 2 ilk mumunu işledi;
  - radar metinleri okunabilir; `/system` hatasız.
  - Sistem sekmesindeki başıboş `null` metni düzeltildi: görünümler artık boş değerleri eleyen `show()` yardımcısından geçiyor. Regresyon testi eklendi.
- **Güncellenen belgeler:** `cohort-1-activation-report.md` (kapanış), `roadmap.md` (rev. 5.6), `requirements.md` durum satırı.

## 2026-10-21 — M5b Kapı C: migration sonrası ilk döngü
- **Kullanıcının `-ChecksOnly` koşusu:**
  - `paperTrading=3/3`, reconciliation temiz;
  - son döngü `degraded` (önceki `error` giderildi).
- **Beklenen neden `stale-data`:** Kohort 1'in run'ı 52 günlük ısınma mumlarını sırayla tamamlıyor; Kohort 0'ın ilk açılışında da aynısı oldu. Bu bir çıkarım; teyit bir sonraki kontrolle yapılacak.
- **Script'ler:** `-ChecksOnly` ve `m5-update.ps1` artık döngünün `degraded` neden kodlarını da yazıyor.

## 2026-10-21 — M5b Kapı C: güncelleme sonrası doğrulama
- **`m5-update.ps1` (kullanıcı koşusu):**
  - migration 0008 uygulandı;
  - `-ChecksOnly` sonucu: `paperTrading=3/3`, reconciliation temiz, D29 0,70 / 0,60 / 0,54.
- **Operasyonel rapor:**
  - 34 döngü, gecikmeli döngü 0, sonuçlar `ok:29 degraded:3 error:2`;
  - reconciliation 33/33 temiz; duplicate dolum 0;
  - audit: `cycle_error:2`, `paper_run_created:2`, `risk_config_version_added:1`;
  - bellek: Trading.Host 48,86 MiB / 384 MiB.
  - İki hatalı döngü, 0008 uygulanmadan önceki döngüler (`ops_actions.run_id` yoktu).
- **Kabul:** Kohort 1'in altı kabul kriteri karşılandı (`cohort-1-activation-report.md` §4). Açık olan tek nokta, migration sonrası ilk döngünün `ok` olduğunun teyidi.
- **`m5-update.ps1` düzeltmesi:**
  - Kontroller artık, yeniden başlatmadan sonra biten ilk döngüyü bekliyor. Önceki koşuda kontroller açılış döngüsünden önce çalıştı ve eski container'ın son `error` sonucunu gösterdi.
  - Bu ilk döngü `error` ise script durur.
  - Trading.Host zaten açılışta beklemeden bir döngü çalıştırıyor; host kodu değişmedi.

## 2026-10-21 — M5b Kapı C: dashboard düzeltmeleri ve yeni tasarım (kullanıcının tarayıcı kontrolü)
- **`/system` HTTP 500:** en olası neden, migration 0008'in canlı `ops` veritabanına uygulanmamış olması; hiçbir script `m5-migrate.ps1`'i çağırmıyordu. 0008 olmadan:
  - `ops_api` AI ve audit tablolarını okuyamaz;
  - paper trader'ın her döngüde okuduğu `ops_actions.run_id` kolonu yoktur, bu yüzden Run 2 hiç adım atmamış olabilir.

  Yapılanlar:
  - `/system` artık eksik bölümleri `unavailable` listesinde bildiriyor ve trading sağlığını yine gösteriyor;
  - diğer uç noktalar bu durumda 503 ve migration yönlendirmesi döndürüyor;
  - yeni kullanıcı script'i `scripts/m5-update.ps1` sırayla migration'ları uygular, Trading.Host'u canlı veriyle yeniden derleyip başlatır, `-ChecksOnly` kontrolünü ve paper raporunu çalıştırır;
  - deny kuralları ve CLAUDE.md güncellendi.
- **Nakit toplamı:** ilk mumunu işlememiş bir run'ın nakdi 0 sayılıyordu (equity 20.000, nakit 10.000). Artık başlangıç sermayesi sayılıyor; run kartında "İlk 4h mum kapanışı bekleniyor" gösteriliyor (`awaitingFirstBar`).
- **Radar:**
  - koşullar okunabilir Türkçe adlarla gösteriliyor;
  - sıfırı kesen göstergelerde yüzde yerine fark veriliyor (MACD'de −816 % gibi anlamsız değerler kalktı);
  - "Sinyal aktif" ayrıştırıldı: "Yeni giriş sinyali", "Giriş koşulları sağlandı", "Kesişim bekleniyor", "Trend sürüyor";
  - dolumun bir sonraki 4h açılışta gerçekleştiği açıklanıyor; hücre ayrıntısında son L2 kararı görünüyor;
  - matrise son kapanış sütunu eklendi; gösterge tablosu gruplandı.
- **Görsel tasarım (`ui-ux-pro-max`):** koyu varsayılan quant terminal teması ve açık tema düğmesi, `tabular-nums`, SVG ikonlu rozetler, mikro çubuklar, her sekmede bilgi şeridi, geri sayımlar, okunabilir strateji adları. CSP ve harici kaynak yasağı korundu.
- Testler: radar adları, uzaklık kuralı ve giriş türü; ilk mumu bekleyen run; grant'ler olmadan `/system` ve 503.

## 2026-10-21 — M5b Kapı C: Kohort 1 aktivasyonu
- **Kayıt (kullanıcı koşusu):**
  - Kohort 1 holdout'ta cohort 2 olarak kaydedildi: `registered_at` 2026-10-21 17:41:45 UTC, `A_1` 2026-10-23, pencere sonu 2027-10-23.
  - Manifesto hash'i `3d5f7c95…`; `protocol_complete = t`, `underpowered = t` (en iyi sonuç `Inconclusive`).
- **Parity (`R`):**
  - L1 `8dccb8f86868` ve L2 `3bcc41534577` birebir; reconciliation temiz.
  - Rapor SHA-256 `2f234be0…6717e`, kuru koşuyla aynı.
  - Ardından `Frozen → PaperTrading`.
- **Paper ve risk:** Trading.Host canlı veriyle yeniden başladı. D29 `RiskConfig`'te: 0,70 / 0,60 / 0,54.
- **Yeni belge:** `docs/cohort-1-activation-report.md`.
- **Güncellenen belgeler:** `cohort-1-preregistration.md`, ADR-0019 durumu, `roadmap.md` (M5b).
- **Script düzeltmeleri (`m5-cohort1-activate.ps1`):**
  - tr-TR'de `-match` ile anahtar ayrıştırma hatası düzeltildi (`decc1b2`).
  - `-CohortId` ile kayıttan sonra devam edilebiliyor.
  - Windows PowerShell'in JSON dizisini tek nesne döndürmesi yüzünden yaşam döngüsü sayımı tüm kohortları topluyordu (`5/3`); düzeltildi.
  - Yeni salt-okunur `-ChecksOnly`.
- **Açık kalanlar (kullanıcı):**
  - `-ChecksOnly` kontrolü;
  - dashboard'un tarayıcıda kontrolü;
  - ilk operasyonel raporda gecikmeli döngü, reconciliation ve bellek.

## 2026-10-21 — M5b Kapı B: çevrimdışı kod ve testler
- **Kohort başına paper run (ADR-0019):** `PaperTrader`, `PaperTrading`'e geçen ve run'ı olmayan her kohort için ayrı run açar; mevcut run'lar değişmez. Isınma `max(45 gün, en uzun gösterge + 1 bar + 1 gün)` (H08: 52 gün). Pozisyon kapatma run'a özgü (`ops_actions.run_id`). Canlı veri, daha uzun ısınma için önceki mumları da tamamlar.
- **Kohort 1 hazırlığı:** `cohort-manifest` üretici ve `docs/cohort-1.spec.json`. Yeni deneme açılmaz; no-halt koşusu M4 sonuç hash'ini yeniden üretmelidir. Research.Host `job submit-cohort` (yalnızca submit token). Evaluator kodu değişmedi.
- **Kernel:** `DslStrategy.Inspect` (salt okunur). M2 hash'leri, motor regresyonu ve Kohort 0 parity testleri değişmeden geçti.
- **M9a (ADR-0020):** sinyal radarı (11 gösterge, BTC rejimi, interpreter'a dayalı yakınlık), `/dashboard` ve salt-okunur veri uç noktaları, güvenlik başlıkları, loopback `Host` kuralı (421), token karşılaştırmasında sabit uzunluklu özet. Migration 0008: `ops_api`'ye yalnızca `SELECT` grant'leri ve `research.frozen_provenance` view'ı; yeni tablo yok.
- **Ön yüz:** kullanıcı isteğiyle okunabilirlik gözden geçirmesi (odak göstergesi, kontrast, işaretli PnL, zaman biçimi).
- **Script'ler (kullanıcı):** `m5-cohort1-release.ps1`, `m5-cohort1-activate.ps1`; Claude Code için deny kuralları ve CLAUDE.md güncellendi.
- **Sınırlama:** `HoldoutEnvironmentVerified` ops'tan okunamadığı için dashboard'da gösterilmez (ADR-0020 notu).
- `verify.ps1` yeşil: **518 test** (Architecture 90, MarketData 97, HoldoutEvaluator 46, Trading 31, Infrastructure 24, Backtest 119, Ai 111); build 0 uyarı / 0 hata; secret scan geçti.

## 2026-10-21 — M5b Kapı A (kullanıcı onayı): Kohort 1 (operasyonel) ve M9a dashboard
- **Kullanıcı kararları (U):**
  - **D31:** Kohort 1 = H08 `n150-b300`, H04 `f12-s26-g12`, H07 `n50-v2p5`; yalnızca operasyonel (`underpowered: true`, gerekçe `exemptions`'ta, `Validated` olamaz); kendi paper run'ı; D29 yalnızca `RiskConfig`'te.
  - H12 yerine H04 seçildi (BTC rejim filtresi tek üyede; MACD ailesi tekrarlanmıyor). Not: manifestonun değerlendirdiği `policy` koşusunda M_R: H08 −4,2 %, H04 0,2 %, H07 −1,2 %.
  - **D32:** M9'un bugünkü veriyle karşılanabilen kısmı ve sinyal radarı öne çekildi (M9a).
- **Belgeler:**
  - yeni: ADR-0019 (operasyonel kohort, kohort başına paper run, kayıt istemcisi), ADR-0020 (yerel dashboard ve radar), `docs/cohort-1-preregistration.md`;
  - güncellenen: `requirements.md` (D31, D32, §4 dashboard notu), `roadmap.md` (M5b).
- Kod değişikliği yok.

## 2026-10-20 — M5 kapanışı (kullanıcı kabulü)
- **Kullanıcı kararları (U):**
  - **D29 (P1):** operasyonda bağlayıcı küme maruziyet tavanı %60 (ρ* = 0,70, ortalama bağlantılı kümeleme, 30 günlük getiriler), uyarı %54.
  - **D30:** `PaperTrading → TestnetActive` için ≥ 14 gün, ≥ 60 işlenmiş 4h mum ve son 7 günde sıfır kritik hata.
  - **M5 tamamlandı:** 7 günlük paper kriteri, kapanış sonrası arka planda süren operasyonel gözleme bırakıldı (kabul edilmiş sapma).
- **Kapı C'nin son koşuları (kullanıcı):**
  - paper başlatıldı; restart, `calibrate-quick` ve kill switch denemeleri yapıldı;
  - şifreli yedek alındı; geri yükleme 33/33 tablo `MATCH`;
  - ilk operasyonel rapor: 4 döngü, gecikmeli döngü 0, reconciliation 2/2, duplicate 0, kesinti yok.
- **Bellek (T):** Trading.Host ≈ 40 MiB / 384 MiB, PostgreSQL ≈ 293 MiB / 1 GiB; limitler korundu.
- **Kod:**
  - Kernel'de P1 değerleri (`RiskConfig.P1ClusterCap`, `P1ClusterWarning`).
  - `RiskConfigDocument`'e isteğe bağlı küme tavanı alanları. Boşken yazılmadıkları için eski belgelerin JSON'u ve hash'i aynı kalıyor. Tavanı kaldırmak ya da yükseltmek gevşetme sayılıyor.
  - Yeni paper koşuları tavanla başlıyor. Korelasyon uyarısı audit'e yazılıyor (`correlation_warning`).
  - Parity replay tavanlı yapılandırmayı kullanıyor; L3'ün izole koşusu manifestonun kendi risk bloğunu kullanıyor.
  - Tavanlı parity tekrarı (`R`, dry run): L1/L2 birebir; tavan hiç bağlamadı.
  - Yeni testler: `RiskConfigDocumentTests`.
- **Belgeler:**
  - yeni: `docs/m5-acceptance-report.md`;
  - güncellenen: `requirements.md` (D29, D30, §7, §8), `risk-model.md` (§2 küme tavanı, durum), `roadmap.md` (M5 ✅), ADR-0012/0013/0014/0015 uygulama durumları.
- `verify.ps1` yeşil: **492 test**; secret scan geçti.
- **Kullanıcıya kalan:** çalışan paper koşusuna tavanı eklemek (Trading.Host'u yeniden derleyip yeni `RiskConfig` sürümü eklemek; kabul raporu §4).

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
