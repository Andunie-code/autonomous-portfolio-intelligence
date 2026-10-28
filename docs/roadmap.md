# Yol Haritası

Son güncelleme: 2026-10-26 (rev. 5.7; M6 başladı, Kapı A onaylandı) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

> Durum: **M0 yerelde tamamlandı** (2026-10-13; doğrulama `716a1bd` commit'inde, temiz klonda). Uzak depo: private GitHub deposu `Andunie-code/autonomous-portfolio-intelligence`. M1 ve sonrası başlamadı; her milestone ayrı kullanıcı onayı gerektirir. Bir milestone, kabul kriterlerindeki testler **gerçekten uygulanıp geçtiğinde** tamamlanmış sayılır.

## Sıralamanın mantığı
1. Önce kanıt altyapısı (veri, backtest, değerlendirme), sonra para hareketi.
2. Sistem LLM olmadan tam işlevli olmalı. LLM görev sınıfları ancak nitelik kazanınca devreye girer.
3. Gerçek holdout ortamı M5'te aynı yerel makinede kurulur (D5 rev., ADR-0013; VPS yok). O zamana kadar gerçek holdout değerlendirmesi yapılmaz (T3). Testnet doğrulamaya bağlı değildir. İlk `Validated` sonucu en erken, kohortun dondurulmasından 6 ay sonra gelir.
4. M4 (AI hattı) ile M5–M6 (operasyon hattı) M3'ten sonra paralel ilerleyebilir.

```
M0 → M1 → M2 → M3 ─┬─ M4 ──────────────┬─ M7
                   └─ M5 ── M6 ─────────┴─ M8 ── M9 ── M10
```

Göreli büyüklük: S/M/L. Süre tahmini verilmez.

## İlk dikey dilim (D15): M0 + M1 + M2
Veri doğruluğu, iki baseline, benchmark'lar, maliyet modeli ve deterministik backtest testleri. LLM, emir, Testnet ve dashboard yok.

**Uygulama ilkesi (T23, T24):** M1 ve M2 kodlanırken M3+ bileşenleri (DSL, kohort, evaluator, LLM, `ops`/`holdout` veritabanları) koda erken eklenmez (YAGNI). Model adları kodda sabit yazılmaz, konfigürasyondan okunur.

---

### M0 — Temel (S) — ✅ yerelde tamamlandı (2026-10-13)
- **Kapsam:**
  - git deposu (yerel; uzak repo olarak private GitHub deposu; push her seferinde kullanıcı onayıyla);
  - solution iskeleti: kernel, modüller, iki host;
  - docker-compose ile dev PostgreSQL; migration altyapısı;
  - **tek komutluk yerel doğrulama script'i** (build, test, mimari testler; T16);
  - JSON loglama ve redaction;
  - **Claude Code korkulukları (T17):** proje talimat dosyası ve proje izin kuralları;
  - `docs/CHANGELOG.md`'nin git'e geçmesi.
- **Kabul kriterleri:**
  - Temiz makinede tek komutla ortam ayağa kalkıyor ve migration'lar çalışıyor.
  - Doğrulama script'i yeşil.
  - Mimari test: Research projeleri Execution'a, hiçbir proje holdout secret'ına referans veremiyor.
  - Kanarya secret'ı loglarda maskeli görünüyor.
  - Talimat dosyası ve izin kuralları repoda; içerikleri `architecture.md` §10 ile uyumlu.

### M1 — Veri temeli (M) — ✅ tamamlandı (2026-10-14; kullanıcı kabulü)
- **Politika:** [`m1-data-policy.md`](m1-data-policy.md) v1.1 (kullanıcı onaylı; onay, belgenin eklendiği commit'e bağlı).
- **Sonuç:** çekirdek evren [`m1-core-universe.md`](m1-core-universe.md) (`core-4y-S`, snapshot `a6c4e89b…`). Kabul kriterleri: 1–5 ve 7 testlerle (`verify.ps1`, 140 test) ve gerçek veride H1 = 0 ile; 6 ve 8 gerçek veri kabul raporu ve kullanıcı onayıyla. Açık konu: üç paritenin Aralık 2021 1h ısınma dosyası (M2 öncesi karar).
- **Kapılar (U):** Kapı 1 = politika ve tüm çevrimdışı kod/testler, `verify.ps1` yeşil. Kapı 2 = gerçek `data.binance.vision` erişimi (`--dry-run`, ardından gerçek import), kullanıcının tek aşamalı etkileşimli onayıyla. Çekirdek 20 parite, kabul raporunun kullanıcı tarafından onaylanmasıyla kesinleşir.
- **Kapsam:**
  - Binance public data import (1h, 4h); checksum; ms/µs normalizasyonu; OHLC kontrolleri; boşluk raporu; `DatasetSnapshot`.
  - **Araştırma kesim tarihi** `C = 2026-01-01T00:00:00Z`: tek doğruluk kaynağı (migration ve kod), "tam aralık" mum üyeliği, `market.candles` üzerinde `CHECK`, dosya planlayıcı ve HTTP koruması (T4, T6).
  - **Çekirdek parite evreni:** kayıt zamanına göre seçim (`U = 2022-01-01`), elenenler ve hayatta kalma yanlılığı raporu. E1 adayları **yalnızca listelenir**; verileri import edilmez (ADR-0012).
  - Tüm testler çevrimdışı (sahte zip ve handler). Gerçek indirme ayrı bir CLI komutudur: önce `--dry-run`, sonra gerçek indirme.
- **Kabul kriterleri:**
  1. `C` hem migration'da hem kodda aynı; bir test eşitliği doğruluyor.
  2. Üyelik testleri (1h, 4h, 1d, 1w, 1M): 2025-12-31T23:00Z açılışlı 1h mum dahil; 2026-01-01T00:00Z açılışlı 1h mum hariç; 2025-12-29 açılışlı 1w mumu hariç; 2025-12-31 açılışlı 1d mumu dahil.
  3. UTC: ms ve µs girdileri aynı anı üretiyor; ayrıştırılan değerlerin offset'i 0; DB oturumu `Europe/Istanbul` olsa bile `CHECK` doğru karşılaştırma yapıyor; testler UTC dışı makine saat diliminde de geçiyor.
  4. **H1, H2, H2b, H3, H4** geçiyor. Kesimden sonraki satır sayısı 0; önbellekte kesimden sonrasını kapsayan dosya yok.
  5. HTTP: kesim tarihine uzanan aralıklar ağa gitmeden reddediliyor; host allowlist'i ve redirect reddi uygulanıyor; testlerin ağa çıkmadığını doğrulayan bir test var.
  6. 20 çekirdek parite için `[2022-01-01, C)` aralığında kalite şartlarını sağlayan 1h ve 4h veri. 20'ye ulaşılamazsa **sessizce gevşetilmez;** rapor hazırlanır ve durulur.
  7. Bozuk ya da eksik checksum reddediliyor; import idempotent; hash kararlı; 1h ve 4h tutarlılığı kontrol ediliyor ve anomaliler raporlanıyor (sessizce düzeltilmez); boşluk raporu üretiliyor.
  8. Gerçek veri kabul raporu (kapsam, boşluklar, hash, H1, hayatta kalma yanlılığı, anomaliler, önerilen çekirdek liste) üretildi ve kullanıcı tarafından onaylandı.

### M2 — Baseline backtest (L) ← ilk dilimin sonu — ✅ tamamlandı (2026-10-14; kullanıcı kabulü)
- **Kapsam:**
  - EMA, ATR, Supertrend ve RSI indikatörleri.
  - `IStrategy` ve iki kodlanmış baseline (EMA 50/100 trend, Supertrend).
  - Versiyonlu maliyet modeli (stop dolumları için ayrı slippage parametresi dahil).
  - **Versiyonlu varsayılan allocation politikası (T11):** onaylı tavanlar içinde ters volatilite.
  - **Çıkış modeli (T20, ADR-0014):** kapanış sinyalleri `t+1` açılışında uygulanır; mum içi koruyucu stop OHLC kuralıyla simüle edilir; efektif stop = strateji stop'u ile `RiskConfig` hard stop'undan (D25) girişe en yakın olanı.
  - Tek ve çoklu varlık backtest; benchmark'lar; deney kaydı ve rapor.
- **Ön koşul:** D25 hard stop değeri (%10, karara bağlandı) ve ön kayıt [`m2-protocol.md`](m2-protocol.md) v1.
- **Durum:** tamamlandı; sonuçlar [`m2-baseline-results.md`](m2-baseline-results.md). İlk dikey dilim (D15: M0–M2) tamamlandı.
- **Kabul kriterleri:**
  - Golden testler, look-ahead property testi, muhasebe invariant'ı, bit bit determinizm ve benchmark analitik eşleşmesi geçiyor.
  - Allocation politikası property testleri (tavan aşımı yok) geçiyor.
  - Stop kuralı testleri geçiyor (gap, `Low ≤ S`, aynı mumda stop önceliği, mum içinde seviye değişmezliği; `testing-strategy.md` §3).
  - İki baseline için maliyetler düşülmüş, benchmark karşılaştırmalı rapor üretiliyor.

### M3 — DSL, değerlendirme hattı ve evaluator (yerel) (L) — ✅ tamamlandı (2026-10-16)
- **Ön kayıt:** [`m3-protocol.md`](m3-protocol.md) v1 ve ADR-0016 (Kapı A, kullanıcı onayı).
- **Durum (2026-10-16):** Kapı A (`9928531`) ve Protokol v2 (`5668d12`) ön kayıtlı. Kapı B: kod ve testler. Kapı C: E1 alt kümesi, kullanıcı kabulü ([`m3-e1-subset.md`](m3-e1-subset.md)). v2 kalibrasyonu geçti ([`m3-calibration-results-v2.md`](m3-calibration-results-v2.md)). Kapı D: Kohort 0 donduruldu (`be2bce3`) ve sentetik evaluator'a kaydedildi. M5'e devredenler: evaluator image'ı ve container, V9, V14'ün Trading tarafı.
- **Kapılar (U):** A = Protokol v1 ve ADR-0016 onayı; B = çevrimdışı kod ve testler, `verify.ps1` yeşil; C = E1 alt küme import'u (kullanıcının etkileşimli Kapı-2 koşusu); D = Kohort 0 dondurma ve M3 kabul raporu.
- **Kapsam:**
  - DSL ve validator, interpreter.
  - **Genişletilmiş indikatör havuzu (U, D27):** SMA, MACD, ADX, Bollinger, Donchian, ROC, Volume SMA; BTCUSDT piyasa rejim filtresi (ADR-0016).
  - Walk-forward, bootstrap, parametre komşuluğu, Deflated Sharpe.
  - **Aile kimliği** (normalizasyon, yapı hash'i, aile temsilcileriyle benzerlik kontrolü; ADR-0015) ve deneme sayacı.
  - **Kohort registry'si:** değiştirilemez manifesto; `registered_at` ve `A_c`'nin sunucu tarafından atanması; sınırların yetkili kayıtta uygulanması. GitHub push'u isteğe bağlıdır ve kullanıcı onayına tabidir.
  - **Ayrı sonuç tabloları** (tanısal ve ileriye dönük); trigger yalnızca ileriye dönük tablolarda.
  - **İki izli yaşam döngüsü** (ADR-0012): operasyonel iz ve türetilmiş doğrulama durumu.
  - **Protokol v1:** zorunlu alanlar; bloke edici değerler kullanıcı kararıyla (B5 → D26).
  - **Süre protokolü aracı:** güç simülasyonu, rejim etiketleri, "gücü yetersiz" işareti.
  - **Evaluator** (tanısal ve ileriye dönük): yerelde ayrı bir container, ayrı roller, **yalnızca sentetik veri.**
  - Sınırlar: K_D, açık kohort sınırı, aile kotası. Sonuç ve `Invalid` kuralları.
  - E1 kategorisi (sentetik testlerle) ve E1 adaylarından ilk 5 paritenin import'u (U, D28).
  - Kapı eşiklerinin sentetik kalibrasyonu.
- **Kabul kriterleri:**
  - Yeni indikatörlerin golden ve look-ahead testleri, rejim filtresinin look-ahead testi geçiyor; M2'nin dört sonuç hash'i engine değişikliklerinden sonra aynı.
  - DSL ve baseline parity'si sağlanıyor; kalibrasyon protokolü kaydedildi; yanlış pozitif ve doğru pozitif testleri geçiyor.
  - **Manifesto:**
    - şema doğrulaması; eksik protokol "doğrulamaya uygun değil" olarak işaretleniyor;
    - `A_c` = `registered_at` + 24 saatten sonraki ilk UTC gece yarısı (sunucu tarafından atanır; Kohort 0 istisnası architecture §6.3);
    - dondurulmuş kohortta değişiklik reddediliyor; değiştirilmiş strateji yeni kohort ve `A > now` alıyor.
  - **Aile kotası:** Parametre değişikliği ve sonucu etkileyen engine değişikliği aynı aile sayılıyor; 3. deneme reddediliyor; benzerlik kontrolü bir kopyayı yakalıyor (pozitif kontrol); karşılaştırma yalnızca aile temsilcileriyle yapılıyor ve bellek kullanımı aile sayısıyla doğrusal kalıyor.
  - **Sınırlar:** K_D, 2 × 3 açık kohort ve aile kotası aşılınca istek reddediliyor.
  - **`Validated` yazımı:** `Validated` durumunu sonuç kaydı dışında set etmenin her yolu reddediliyor; paper ve Testnet verisi doğrulama hesabına girmiyor; gücü yetersiz ya da protokolü eksik kohort `Validated` olamıyor; işlem sayısı yetersizse sonuç `Inconclusive`.
  - **Erken değerlendirme ve import:** enjekte edilmiş saatle reddediliyor (uygulama katmanı ve DB trigger'ı ayrı ayrı test edilir).
  - **`Invalid`:** yalnızca kanıtla veriliyor; yeniden değerlendirme yalnızca aynı manifesto, pencere ve digest ile yapılabiliyor ve sınırı var; tüm koşular audit'te.
  - **Roller:** `eval_diagnostic` ve `eval_prospective` birbirinin şemasını okuyamıyor; Research ve Trading rolleri holdout şemalarını okuyamıyor.
  - **Tekrar üretilebilirlik:** aynı girdiler aynı sonuç hash'ini üretiyor.
  - **Değiştirilemezlik:** kohort silinemiyor, geri çekilemiyor; operasyonel devre dışı bırakma sonucu değiştirmiyor.
  - **H7–H11, H15–H18** sentetik ortamda geçiyor.
  - **Kohort 0** (baseline'lar), **yalnızca Protokol v1 onaylandıktan sonra** dondurulup yerel commit'le kaydedildi. Onaydan önce dondurulursa "yalnızca operasyonel" olarak etiketlenir.
  - Tanısal rol ileriye dönük sonuç tablosuna yazamıyor; manifesto kaydı trigger'a takılmıyor; istemcinin gönderdiği `A_c` reddediliyor; sınırlar Registry atlatılsa bile yetkili kayıtta uygulanıyor (V11–V13).
  - Registry'deki strateji sürümleri değiştirilemiyor; hash uyuşmazlığında Trading stratejiyi çalıştırmıyor (V14).
- **Not:** Gerçek holdout verisi bu milestone'da hiçbir yere indirilmez.

### M4 — ModelRouter, yeterlilik altyapısı ve Researcher (M) — ✅ tamamlandı (2026-10-19; kullanıcı kabulü)
- **Kapılar (U):**
  - **A:** M4 ön kaydı ([`m4-protocol.md`](m4-protocol.md) v1, [`m4-hypotheses.v1.json`](m4-hypotheses.v1.json)) ve ADR-0017; Gemini belgelerinin yeniden doğrulanması (`sources.md`, 2026-10-18).
  - **B:** tüm çevrimdışı kod; sahte sağlayıcıyla §6.3 testleri ve Researcher sözleşme testi; gerçek veride (`< C`) 12 hipotezin ve 96 denemenin LLM'siz koşusu; `verify.ps1` yeşil; 0 USD harcama.
  - **C (B1 gerekli):** gerçek Gemini spike'ı, pilot, HypothesisGeneration ön kaydı ve yeterlilik koşusu, mutabakat raporu. Tüm çağrılar kullanıcının etkileşimli koşusudur.
- **Durum (2026-10-19): tamamlandı.**
  - **Kapı A:** `32cf7c5`.
  - **Kapı B:** `5950c81`; LLM'siz koşuda 12 hipotez ve 96 deneme, aday yok ([`m4-llmless-results.md`](m4-llmless-results.md)).
  - **Kapı C (B1 kapandı):**
    - spike 5/5 ([`m4-spike-results.md`](m4-spike-results.md));
    - pilot ([`m4-hg-pilot-results.md`](m4-hg-pilot-results.md));
    - HypothesisGeneration yeterliliği **`Failed`** (`TaskQualification` 1; [`m4-hg-qualification-results.md`](m4-hg-qualification-results.md)).
  - Toplam AI harcaması 0,146228 USD ([`m4-ai-reconciliation-2026-10.md`](m4-ai-reconciliation-2026-10.md)). Google faturasıyla mutabakat ay sonunda yapılır.
  - **Kabul kriterleri:**
    - §6.3 testleri sıfır maliyetle geçti;
    - Researcher sözleşme testi geçti;
    - spike kaydedildi;
    - pilot, protokol, koşu ve `TaskQualification` tamamlandı;
    - LLM'siz modda ≥ 10 hipotez işlendi;
    - harcama < 10 USD ve mutabakat raporu üretildi.
- **Kullanıcı kararları (2026-10-18):** LLM'siz koşuda global deneme sayacı ≤ 100; hipotezleri Claude Code önerir, kullanıcı onaylar (köken `dev-assisted`/`grid`, kontaminasyon bayrağı).
- **Ön koşul:** **B1** (kredinin ücretli katmana uygulanması). Gemini fiyatları, rate limit'leri ve SDK yeniden doğrulanır ve `sources.md` güncellenir. Altyapı işi B1 olmadan başlayabilir; yalnızca yeterlilik koşusu B1'i bekler.
- **Kapsam:**
  - `IAiProvider` ve `GeminiProvider`; `ModelRouter` ve görev politikaları.
  - `DecisionTrace`, `EvidenceValidator`.
  - Çok katmanlı tavanlar; **LLM acil durdurma ve otomatik tetikler**; maliyet defteri ve aylık mutabakat raporu.
  - Ön kayıt harness'ı (protokol hash'i şartı), `TaskQualification`.
  - Researcher tool'ları; LLM'siz araştırma modu (grid ve insan onaylı hipotezler).
- **Kabul kriterleri:**
  - §6.3'teki ModelRouter ve harcama kontrolü testleri sahte sağlayıcıyla, **sıfır API maliyetiyle** geçiyor.
  - Researcher sözleşme testi geçiyor: holdout, evaluator, SQL, shell ve web erişimi yok.
  - Teknik spike (sentetik) yapıldı ve sonucu kaydedildi.
  - **HypothesisGeneration:** pilot yapıldı, protokol kaydedildi, yeterlilik koşusu yapıldı, sonuç (geçti, kaldı ya da belirsiz) `TaskQualification`'a yazıldı. Nitelik kazanılması kabul kriteri **değildir**; sürecin doğru işletilmesi kriterdir.
  - LLM'siz modda en az 10 hipotez uçtan uca işlendi (boru hattı kriteri, istatistiksel değil).
  - Toplam harcama 10 USD'lik başlangıç tavanının altında kaldı; mutabakat raporu üretildi.

### M5 — Yerel holdout ortamı ve deterministik paper trading (L) — ✅ tamamlandı (2026-10-20; kullanıcı kabulü)
- **Durum:** Kapı A (`9dda841`), Kapı B (488/488) ve Kapı C tamamlandı. Kabul raporu: [`m5-acceptance-report.md`](m5-acceptance-report.md).
  - **Kabul edilen sapma (U):** 7 günlük paper kriteri, kullanıcı kararıyla M5 kapanışından sonra arka planda süren operasyonel gözlem olarak devam ediyor. Kapanışta paper yeni başlamıştı.
  - **Kararlar:** P1 → D29 (%60 küme tavanı, uyarı %54); asgari paper süresi → D30.
- **Kapılar (U):** **A** = tasarım ve ön kayıt; **B** = çevrimdışı kod ve testler, `verify.ps1` yeşil; **C** = gerçek yerel Docker ortamı (bootstrap, H-ENV ve `HoldoutEnvironmentVerified`, Kohort 0'ın gerçek holdout DB'ye aktarılması, `R` üzerinde parity replay, ≥ 7 gün paper, yedek ve geri yükleme, P1 raporu). Ortam, H-ENV, paper ve yedek adımları kullanıcının etkileşimli koşularıdır.
- **Ön koşul:** yok (B2 kaldırıldı; D5 rev., VPS yok).
- **Kapsam (ADR-0013):**
  - Tek PostgreSQL instance'ında `ops` ve `holdout` veritabanları; `REVOKE CONNECT FROM PUBLIC`, rol bazlı `CONNECT`, `pg_hba` kuralları.
  - Dört Docker ağlı Compose; her servis için `mem_limit` ve `cpus`; Research.Host ve evaluator iş bazlı; etkileşimli yerel deploy (T19).
  - `.claude/settings.json` ve `CLAUDE.md` holdout korkulukları (T17) gözden geçirilir.
  - ops DB: `market.live_candles` (yalnızca Trading), `research.*` view'ları (`candles`, `revealed_cohorts`, `revealed_paper_summary`) ve tüm DB rolleri (architecture §6.2).
  - Holdout DB ve evaluator: kohort penceresine bağlı import ve erken import'u reddeden DB trigger'ı. Holdout verisini **yalnızca evaluator**, kaynaktan import eder. Evaluator `status_relay` rolüyle `research.cohort_status`'a yazar (D24).
  - Trading.Host: canlı veri, ledger, risk (`ExperimentalPairsMaxExposure` ve koruyucu stop dahil), simüle execution, reconciliation, kill switch, audit, Ops API (yalnızca loopback; açık kohortların PnL'i görünür; manifesto ve sonuç değiştirilemez).
  - Yedek ve geri yükleme (makine dışı kopya).
  - Korelasyon izleme ve P1 için tarihsel etki analizi.
- **Kabul kriterleri:**
  - **H-ENV (H5, H6, H6b, H14, H19–H28)** gerçek yerel ortamda geçti; ardından `HoldoutEnvironmentVerified` kullanıcı oturumuyla set edildi ve audit'e yazıldı. Bu bayrak yalnızca `Validated` yazımını kapılar.
  - Research rolü canlı mumları ve açığa çıkmamış paper sonuçlarını okuyamıyor; `research.cohort_status`'a yazamıyor; `eval_*` rolleri `ops` veritabanına bağlanamıyor; erken import reddediliyor (gerçek ortamda; H21, H22, H25).
  - `status_relay` kimlik bilgisi yalnızca evaluator'da; Research.Host'ta yok; rol yalnızca `research.cohort_status`'a ekleme yapabiliyor.
  - Tüm servisler bellek sınırları içinde çalışıyor; ağır bir araştırma işi sırasında Trading.Host döngüsü kaçırılmıyor (döngü süresi metriğiyle ölçülür).
  - Deneysel parite tavanı için risk tablosu testleri geçiyor (tavanın tam sınırı, kırpma, çıkışın serbestliği, yetkisiz değişikliğin reddi).
  - Operasyonel güvenlik eylemleri (devre dışı bırakma, kill switch, pozisyon kapatma) çalışıyor ve audit'e yazılıyor; kohort kayıtlarını değiştirmiyor.
  - Parity replay (L1 sinyal ve L2 karar paritesi, ADR-0015) geçiyor; fark raporu (L3) normalize metriklerle üretiliyor; risk tablosunun her satırı test edildi; paper'da stop kuralı backtest ile birebir; bayat veride yalnızca çıkışlar çalışıyor; restart tutarlı (kaçırılan mumlar sırayla işleniyor).
  - 7 gün paper çalışması; makinenin kapalı kaldığı süreler raporlanıyor ve sonrasında reconciliation ile tutarlı devam ediliyor (yerel çalışmada kesintisizlik garanti edilmez, ADR-0013); yedekten geri yükleme denendi.
  - P1 analiz raporu kullanıcıya sunuldu.

### M5b — Kohort 1 ve M9a dashboard (M) — ✅ Tamamlandı (kullanıcı kabulü U, 2026-10-21; 523 test)
- **Kapı C durumu:** [`cohort-1-activation-report.md`](cohort-1-activation-report.md).
  - Kayıt: holdout cohort 2; `A_1` 2026-10-23, pencere sonu 2027-10-23; `underpowered`.
  - Parity: L1/L2 birebir. D29 `RiskConfig`'te.
  - Doğrulama (`m5-update.ps1`): 3/3 `PaperTrading`, gecikmeli döngü 0, reconciliation 33/33, Trading.Host 48,86 MiB. Migration sonrası ilk döngü `degraded`; neden `stale-data` (ısınma mumları) olarak teyit edildi. Dashboard'un beş sekmesi tarayıcıda kontrol edildi. Kapanış: [`cohort-1-activation-report.md`](cohort-1-activation-report.md) §9.
- **Kararlar (U):** D31 (Kohort 1, yalnızca operasyonel; ADR-0019, [`cohort-1-preregistration.md`](cohort-1-preregistration.md)) ve D32 (M9a; ADR-0020).
- **Kapılar (U):** **A** = ADR-0019/0020 ve Kohort 1 ön kaydı; **B** = çevrimdışı kod ve testler (kohort başına paper run, manifest üretici, kayıt istemcisi, Kernel `Inspect()`, radar, dashboard, migration 0008, etkileşimli script'ler), `verify.ps1` yeşil; **C** = kullanıcının koşuları (release bilgisi, manifestonun üretilip onayla commit'lenmesi, `m5-cohort1-activate.ps1`, tarayıcı kontrolü) ve aktivasyon raporu.
- **Kabul kriterleri:**
  - Kohort 1: `cohort-1-preregistration.md` §5.
  - M5 regresyonu: M2'nin dört hash'i, M3 golden'ları ve Kohort 0 parity L1/L2 hash'leri değişmedi; Kohort 0'ın paper run'ı kesintisiz sürüyor.
  - M9a: zincir (dolum → L2 → L1 → strateji → manifesto hash'i → deney → snapshot) gezilebiliyor; DecisionTrace, degraded işaretleri, trading/LLM kill switch'leri ve görev sınıfı bazında maliyet görünüyor; radar 11 göstergeyi, BTC rejimini ve aktif stratejilerin koşul yakınlığını gösteriyor.
  - Güvenlik: H24 route testi genişletildi (yeni POST yok, yasaklı kelime yok, tüm veri uç noktalarında 401, güvenlik başlıkları, yanlış `Host` → 421); `research_app` izinleri değişmedi; yanıtlarda secret yok.

### M6 — Binance Testnet execution (M) — ✅ tamamlandı (2026-10-28; kullanıcı kabulü)
- **Ön kayıt:** [`m6-protocol.md`](m6-protocol.md) v1 ve ADR-0021 (Kapı A, kullanıcı onayı 2026-10-26).
- **Durum:** Kapı A (`b2433bc`), Kapı B (`a19a5e4`, 598 test) ve v1.1 (`86f5f1b`, A-4: Testnet sermayesi 5.000 USDT, 599 test) tamamlandı. Kapı C: C1 anahtar, C2 tatbikat PASS (23 tam yaşam döngüsü), C3 güncelleme. Kabul raporu: [`m6-acceptance-report.md`](m6-acceptance-report.md).
  - **Kabul edilen sapma (U):** gerçek `PaperTrading → TestnetActive` terfisi (C4), D30 dolunca (en erken 2026-11-03) kapanıştan sonraki operasyonel adım olarak yapılır. Bu, M5'teki paper gözlemiyle aynı biçimde ele alındı.
- **Kapılar (U):**
  - **A:** Binance belgelerinin doğrulanması (`sources.md` S-B11…S-B24), protokol ve ADR.
  - **B:** çevrimdışı kod ve testler (sahte handler, fault injection); `verify.ps1` yeşil.
  - **C:** kullanıcının gerçek Testnet koşuları: ≥ 20 emirlik tatbikat; D30 dolduktan sonra `PaperTrading → TestnetActive` geçişi.
- **Ön koşul:**
  - ~~B6~~ → D33 (2026-10-26).
  - Stop emir tipi ve Testnet desteği belgeden doğrulandı (S-B14, S-B18). Sembol bazında destek tatbikatta gözlenir.
- **Kabul kriterleri:**
  - Giriş dolumundan sonra koruyucu stop emri borsaya gönderiliyor; kill switch açıldığında giriş emirleri iptal ediliyor, stop emirleri kalıyor (D11 rev.); stop emri reddi alarm üretiyor ve yeni girişleri durduruyor; Testnet stop sapmaları fark raporunda kategorize ediliyor.
  - Fault injection altında duplicate emir sayısı 0 ve ledger reconcile ediliyor.
  - Testnet'te ≥ 20 emrin tam yaşam döngüsü tamamlandı.
  - Filtre ihlali yapan emir borsaya gitmeden reddediliyor.
  - Anahtar yalnızca Trading.Host'ta.
  - `PaperTrading → TestnetActive` geçişi yalnızca `Frozen` stratejiler için ve kullanıcı oturumuyla mümkün; **doğrulama gerektirmiyor** (ADR-0012). `Validated` olmayan stratejiler "deneysel" etiketi taşıyor.

### M7 — Kontrollü web araştırması (M) — ✅ tamamlandı (2026-10-28; kullanıcı kabulü)
- **Ön kayıt:** [`m7-protocol.md`](m7-protocol.md) v1, v1.1, v1.2 ve ADR-0022 (Kapı A, kullanıcı onayı 2026-10-28).
- **Durum:** Kapı A (`2ff940b`), Kapı B (`e82e4dd`, 711 test) ve Kapı C tamamlandı. Kabul raporu: [`m7-acceptance-report.md`](m7-acceptance-report.md).
  - **Sonuç:** FindingExtraction yeterliliği **`Failed`** (`TaskQualification` 2: 27/30, p = 0,1227, ihlal 0). Kullanıcı kararıyla v2 döngüsü açılmadı; web araştırması kapalı kalır (D20).
  - **Kabul edilen sapma:** C2 (model sürümü spike'ı) koşulmadı; model sürümü eşleşmesi router'ın degraded kontrolüyle görüldü.
- **Kapılar (U):**
  - **A:** arXiv ve Semantic Scholar koşullarının doğrulanması (`sources.md` S-X1…S-X12), allowlist, PDF kütüphanesi, protokol ve ADR.
  - **B:** çevrimdışı kod ve testler; `verify.ps1` yeşil; API maliyeti 0.
  - **C:** kullanıcının koşuları: ≤ 10 T1 makale ve provenance; FindingExtraction pilotu ve yeterliliği; nitelik varsa bulgu → hipotez ve karşılaştırma raporu; mutabakat.
- **Ön koşul:** allowlist kesinleşmiş; arXiv ve Semantic Scholar koşulları doğrulanmış; PDF kütüphanesi seçilmiş.
- **Kabul kriterleri:**
  - Allowlist dışı ve redirect'le ulaşılan URL'lere erişilemiyor.
  - Injection setinde güvenlik değişmezleri ihlal edilmiyor (0).
  - Uydurma alıntılar reddediliyor.
  - Provenance kayıtları eksiksiz.
  - Bulgudan türeyen hipotez normal hattan geçiyor.
  - FindingExtraction yeterliliği ön kayıtla yapıldı ve kaydedildi; nitelik yoksa web araştırması kapalı.
  - Web'li ve web'siz karşılaştırma raporu hazır.

### M8 — Portfolio Allocator (M)
- **Kabul kriterleri:**
  - Limit dışı öneriler clamp ediliyor ya da reddediliyor.
  - Çıkışları engelleme girişimi etkisiz.
  - LLM durdurulduğunda ya da hata verdiğinde varsayılan politika devreye giriyor.
  - Gölge mod ≥ 4 hafta (süre güç analiziyle uzatılabilir) tamamlandı.
  - AllocationProposal protokolü ve sonucu kaydedildi.
  - Aktif moda geçiş yalnızca nitelik varsa ve kullanıcı onayıyla.
  - Allocator ağırlığı yalnızca `Validated` stratejilere veriliyor; diğerlerinde varsayılan politika uygulanıyor.

### M9 — Dashboard (M)
- **Kabul kriterleri:**
  - Emirden veri setine kadar zincir gezilebiliyor.
  - DecisionTrace, degraded işaretleri ve LLM acil durdurma durumu görünüyor.
  - Görev sınıfı bazında maliyet görünüyor.

### M10 — Sağlamlaştırma (S)
- **Kabul kriterleri:**
  - Alarmlar test edildi (trading kill switch, LLM acil durdurma, H-ENV aylık koşusu, bütçe).
  - Runbook'ta bant dışı API anahtarı iptali, makine kapanması ya da yeniden başlatma sonrası devam ve Docker Desktop bellek ayarı dahil.
  - 30 günlük Testnet provası tamamlandı.

## Global "tamamlandı" tanımı
- Doğrulama script'i yeşil.
- Yeni kararlar ADR'de ve CHANGELOG'da.
- Kritik işlemler audit kaydı üretiyor.
- Repoda, logda ya da prompt'ta secret yok.
- `sources.md` güncel.
- "Uygulandı" denilen her kontrolün testi var.
