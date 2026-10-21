# ADR-0019: Operasyonel kohort, kohort başına paper run ve kohort kayıt istemcisi

- Durum: **Kabul edildi (Kapı A, kullanıcı onayı 2026-10-21)** · Tarih: 2026-10-21
- Karar türü: **U** (operasyonel kohort, üye seçimi, doğrulama ekseni, D29'un yeri; D31) ve **T** (run modeli, kayıt istemcisi, ısınma).
- Uygulama durumu: **Kapı B'de çevrimdışı uygulandı (2026-10-21); gerçek ortam Kapı C'de, kullanıcının koşularıyla.**
- Uygulama notları (Kapı B):
  - Run'ın kohortu `paper_runs.config` içindeki sleeve'lerin `cohortId`'sinden okunur. `PaperRunConfig`'e isteğe bağlı `actionsAfter` eklendi: yeni bir run yalnızca kendisinden sonra istenen operasyonel eylemleri uygular. Boşken yazılmadığı için mevcut run'ların JSON'u ve hash'i değişmez.
  - Sleeve indeksleri run'a özgü olduğu için `trading.ops_actions`'a `run_id` eklendi (migration 0008). Birden fazla run varken `POST /positions/close` `runId` ister; tek run varken ona varsayılır. "Tümünü kapat" tüm run'lara uygulanır.
  - Canlı veri, daha uzun ısınma isteyen yeni bir kohort için saklanan ilk mumdan önceki eksik mumları da tamamlar (kesimden önceye asla gitmez).
  - Döngü metriğindeki `bars_processed`, herhangi bir run'ın işlediği farklı bar sayısıdır.
  - Manifest üretici `cohort-manifest` (BacktestCli, research_app): üyeler `docs/cohort-1.spec.json`'daki ön kayıtlı M4 noktalarıdır. Her üyenin zaten deneme olması ve no-halt koşusunun M4'te kaydedilen sonuç hash'ini yeniden üretmesi şarttır. Global deneme sayacı değişirse hiçbir şey yazılmaz. Holm'un en kötü durumu için güç planı α / üye sayısı ile yapılır (Kohort 0: α/2).
  - Kayıt istemcisi: Research.Host `job submit-cohort` (`Registration:Manifest`, `Registration:Url`, `Registration:TokenFile`). Compose'da Research.Host'a yalnızca `submit_token` eklendi; bir mimari test holdout rollerinin, `status_relay`'in ve read-status token'ının orada olmadığını sabitler.
- İlgili: ADR-0006, ADR-0012, ADR-0013, ADR-0015, ADR-0018; `architecture.md` §6.3–§6.5; `cohort-1-preregistration.md`; `m4-llmless-results.md`; D21, D23, D29, D30, D31

## Bağlam
- M4'ün LLM'siz koşusunda 12 hipotezin hiçbiri tüm araştırma kapılarını geçmedi; aday yok (`m4-llmless-results.md`). `architecture.md` §6.4'teki `Candidate → Frozen` geçişi kapıların geçilmesini şart koşar ve belgelerde bunun için bir kullanıcı istisnası tanımlı değildir.
- Kullanıcı (U, 2026-10-21) zengin DSL havuzunu canlı paper'da operasyonel olarak gözlemlemek istiyor. D21'deki ikinci açık kohort hakkı bunun için kullanılacak.
- Kod tarafında:
  - Trading.Host tek bir paper run çalıştırır. Run, açıldığı andaki `PaperTrading` üyeleriyle sabitlenir; sonradan gelen üyeler yok sayılır (`PaperTrader.ProcessAsync`). Run'ın risk bloğu ve manifesto hash'i `members[0]`'dan alınır.
  - Normal kohort kaydı yalnızca evaluator'ın `POST /cohorts` yoludur (submit token, `eval` ağı). Kayıt istemcisi M5'te bilerek yazılmadı (`m5-protocol.md` §10, YAGNI). `seed-cohort0` yalnızca Kohort 0'ın seed istisnası içindir (V13).
  - Mevcut paper ısınması 45 gündür; H08'in BTC SMA(300) göstergesi ≈ 301 × 4h ≈ 50 gün ister.

## Karar

### 1. Operasyonel kohort (U, D31)
- Araştırma kapılarını geçmemiş stratejiler **yalnızca açık bir kullanıcı kararıyla** ve **yalnızca operasyonel** olarak dondurulabilir. Bu bir istisnadır; `Candidate → Frozen` için otomatik yol değişmez.
- Manifestoda:
  - protokol alanları **tam** (Kohort 0 ile aynı Protokol v2 değerleri); hiçbir eşik gevşetilmez;
  - `power.underpowered: true`, güç analizinin sonucundan bağımsız olarak açıkça yazılır; güç artefaktları yine dürüstçe hesaplanıp manifestoya konur;
  - `exemptions` alanı gerekçeyi taşır: hangi kapıların geçilmediği ve kararın tarihi.
- Sonuç: evaluator'ın karar sırasında 5. adım (`underpowered`) uygulanır; kohort en iyi ihtimalle `Inconclusive` olur, **`Validated` olamaz** (`before_result` trigger'ı). `Failed` kuralları (drawdown, üst sınır < δ) geçerliliğini korur.
- Bedeller (kullanıcıya bildirildi): kohort, ikinci açık kohort hakkını pencere + 3 günlük tampon boyunca (~12 ay) tutar; üyelerin aileleri ömür boyu 2 ileriye dönük denemeden birini harcar. Tanım hash'leri M4'te sayıldığı için **yeni deneme açılmaz** (global sayaç 98 kalır).
- Kayıt normal yoldandır: `registered_at` ve `A_1` sunucuda atanır; açık kohort ve aile kotası `eval_registry` tarafından holdout DB'sinde uygulanır.

### 2. D29 manifestoya yazılmaz (U)
D29 metni gereği küme tavanı "dondurulmuş kohort manifestolarına uygulanmaz". Manifestonun risk bloğu Kohort 0 ile aynı altı alanı taşır. Tavan paper run'a `RiskConfig` üzerinden girer (`P1ClusterCap`, `P1ClusterWarning`) ve çalışan sisteme kullanıcının `POST /risk-config` isteğiyle eklenir.

### 3. Kohort başına paper run (T)
- Her kohortun kendi `trading.paper_runs` satırı vardır: sermaye `10.000 / üye sayısı` eşit sleeve'ler, risk bloğu ve manifesto hash'i o kohortun manifestosundan, P1 tavanı eklenmiş olarak. Böylece paper, parity replay'in simüle ettiği yapıyla birebir aynıdır (L1/L2).
- `PaperTrading` durumunda üyesi olan ama run'ı olmayan bir kohort için yeni run açılır. Mevcut run'lar değişmez; Kohort 0'ın run'ı ve D30 süresi kesintisiz sürer.
- Run kimliği `paper_runs.config` içindeki sleeve'lerin `cohortId` değerinden okunur; şema değişmez.
- Kill switch, "tümünü kapat", bayat veri kuralı ve `RiskConfig` sürümleri **tüm run'lara** uygulanır. `RiskConfig` küresel kalır; limitler (ör. `maxOpenPositions`) run başına, yani kohort portföyü başına uygulanır. Bu, portföy genelinde toplam maruziyetin tek run'a göre artabileceği anlamına gelir; toplam görünüm Ops API/dashboard'da verilir.
- Strateji adları kohortlar arasında benzersizdir; devre dışı bırakma adı aktif run'larda arar.

### 4. Isınma (T)
Yeni run'larda ısınma `max(45 gün, ⌈en büyük gösterge ısınması × 4h⌉ + 1 gün)` olur. Mevcut run'lar config'lerinde saklı ısınmayı kullanır. Parity replay aynı fonksiyonu kullanır. Isınma boyunca eksik BTC verisi Kleene `Unknown` üretir (giriş yok, stop çalışır; m3-protocol §2).

### 5. Kayıt istemcisi (T)
- Research.Host'a `submit-cohort <manifest>` komutu eklenir. Yalnızca submit token'ı (`architecture.md` §10) taşır; `eval` ağındaki evaluator'a `POST /cohorts` gönderir ve yanıtı (`cohortId`, `registeredAt`, `startsAt`, `windowEnd`, `eligibleForValidation`) yazdırır.
- Evaluator kodu (`Api.cs`, `Manifest.cs`, `Evaluation.cs`) ve `compose.holdout.yml` **değişmez**. Holdout DB kimlik bilgisi Research.Host'a verilmez.
- Kaydı, freeze-import'u, parity `--record`'u, Trading.Host'un yeniden başlatılmasını ve D29 `RiskConfig` sürümünü kullanıcının etkileşimli `scripts/m5-cohort1-activate.ps1` koşusu sırayla yapar. Claude Code bu script'i çalıştırmaz (deny kuralı).

## Gerekçe
- Kapıyı geçmemiş stratejileri dondurmak, seçici davranış riskini artırır. Bu riski sınırlayan şey `Validated`'ın kapalı olması, kaydın normal yoldan yapılması ve gerekçenin değiştirilemez manifestoda durmasıdır.
- Kohort başına run, parity sözleşmesini (ADR-0015) bozmadan ikinci kohortu ekler ve Kohort 0'ın gözlemini sıfırlamaz.
- Kayıt istemcisi, mimarinin zaten öngördüğü submit-only yoldur; seed istisnasının yeniden kullanılması V13'ü zayıflatırdı.

## Reddedilen alternatifler
| Alternatif | Neden |
|---|---|
| Mevcut run'a sleeve eklemek | Portföy limitleri 5 stratejiyle paylaşılır; paper, kohortun parity replay'inden ayrışır |
| Kohort 0 dahil yeni birleşik run | Kohort 0'ın paper geçmişi ve D30 sayacı sıfırlanır |
| Kohort 1'i `seed-cohort0` yoluyla kaydetmek | Seed istisnası Kohort 0'a özeldir (architecture §6.3, V13) |
| Kohortu doğrulamaya uygun bırakmak | Kapıyı geçmemiş, 96 denemeden seçilmiş adaylar; seçim yanlılığı riski (kullanıcı reddetti) |
| Protokolü bilerek eksik bırakmak | "Eksik parametre sonradan eklenemez"; eksik protokol bir hata durumunu taklit eder. Açık `underpowered` + `exemptions` daha dürüsttür |
| D29'u manifestoya yazmak | D29 metniyle çelişir |
