# Yol Haritası

Son güncelleme: 2026-10-13 (rev. 4) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

> Durum: **M0 yerelde tamamlandı** (2026-10-13; doğrulama `716a1bd` commit'inde, temiz klonda). Uzak depo: private GitHub deposu `Andunie-code/autonomous-portfolio-intelligence`. M1 ve sonrası başlamadı; her milestone ayrı kullanıcı onayı gerektirir. Bir milestone, kabul kriterlerindeki testler **gerçekten uygulanıp geçtiğinde** tamamlanmış sayılır.

## Sıralamanın mantığı
1. Önce kanıt altyapısı (veri, backtest, değerlendirme), sonra para hareketi.
2. Sistem LLM olmadan tam işlevli olmalı. LLM görev sınıfları ancak nitelik kazanınca devreye girer.
3. Gerçek holdout ortamı, D5'teki VPS ile birlikte M5'te kurulur. O zamana kadar gerçek holdout değerlendirmesi yapılmaz (T3). Testnet doğrulamaya bağlı değildir. İlk `Validated` sonucu en erken, kohortun dondurulmasından 6 ay sonra gelir.
4. M4 (AI hattı) ile M5–M6 (operasyon hattı) M3'ten sonra paralel ilerleyebilir.

```
M0 → M1 → M2 → M3 ─┬─ M4 ──────────────┬─ M7
                   └─ M5 ── M6 ─────────┴─ M8 ── M9 ── M10
```

Göreli büyüklük: S/M/L. Süre tahmini verilmez.

## İlk dikey dilim (D15): M0 + M1 + M2
Veri doğruluğu, iki baseline, benchmark'lar, maliyet modeli ve deterministik backtest testleri. LLM, emir, Testnet ve dashboard yok.

---

### M0 — Temel (S) — ✅ yerelde tamamlandı (2026-10-13)
- **Kapsam:**
  - git deposu (yerel; uzak repo olarak private GitHub deposu kullanıcı onaylı, kimlik doğrulama bekleniyor);
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

### M1 — Veri temeli (M) — başlamadı (B4)
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
  7. Bozuk checksum reddediliyor; import idempotent; hash kararlı; 1h ve 4h verisi birbiriyle tutarlı; boşluk raporu üretiliyor.
  8. Gerçek veri kabul raporu (kapsam, boşluklar, hash, H1) üretildi.

### M2 — Baseline backtest (L) ← ilk dilimin sonu
- **Kapsam:**
  - EMA, ATR, Supertrend ve RSI indikatörleri.
  - `IStrategy` ve iki kodlanmış baseline (EMA 50/100 trend, Supertrend).
  - Versiyonlu maliyet modeli.
  - **Versiyonlu varsayılan allocation politikası (T11):** onaylı tavanlar içinde ters volatilite.
  - Tek ve çoklu varlık backtest; benchmark'lar; deney kaydı ve rapor.
- **Kabul kriterleri:**
  - Golden testler, look-ahead property testi, muhasebe invariant'ı, bit bit determinizm ve benchmark analitik eşleşmesi geçiyor.
  - Allocation politikası property testleri (tavan aşımı yok) geçiyor.
  - İki baseline için maliyetler düşülmüş, benchmark karşılaştırmalı rapor üretiliyor.

### M3 — DSL, değerlendirme hattı ve evaluator (yerel) (M)
- **Kapsam:**
  - DSL ve validator, interpreter.
  - Walk-forward, bootstrap, parametre komşuluğu, Deflated Sharpe.
  - **Aile kimliği** (normalizasyon, yapı hash'i, benzerlik kontrolü) ve deneme sayacı.
  - **Kohort registry'si:** değiştirilemez manifesto, GitHub zaman damgası, `A_c` hesaplaması.
  - **İki izli yaşam döngüsü** (ADR-0012): operasyonel iz ve türetilmiş doğrulama durumu.
  - **Protokol v1:** zorunlu alanlar; bloke edici değerlerin önerisi ve kullanıcı incelemesi (B5).
  - **Süre protokolü aracı:** güç simülasyonu, rejim etiketleri, "gücü yetersiz" işareti.
  - **Evaluator** (tanısal ve ileriye dönük): yerelde ayrı bir container, ayrı roller, **yalnızca sentetik veri.**
  - Sınırlar: K_D, açık kohort sınırı, aile kotası. Sonuç ve `Invalid` kuralları.
  - E1 verisinin import'u ve E1 kategorisi.
  - Kapı eşiklerinin sentetik kalibrasyonu.
- **Kabul kriterleri:**
  - DSL ve baseline parity'si sağlanıyor; kalibrasyon protokolü kaydedildi; yanlış pozitif ve doğru pozitif testleri geçiyor.
  - **Manifesto:**
    - şema doğrulaması; eksik protokol "doğrulamaya uygun değil" olarak işaretleniyor;
    - `A_c ≥` F_c + 24 saatten sonraki ilk UTC gece yarısı;
    - dondurulmuş kohortta değişiklik reddediliyor; değiştirilmiş strateji yeni kohort ve `A > now` alıyor.
  - **Aile kotası:** Parametre değişikliği ve sonucu etkileyen engine değişikliği aynı aile sayılıyor; 3. deneme reddediliyor; benzerlik kontrolü bir kopyayı yakalıyor (pozitif kontrol).
  - **Sınırlar:** K_D, 2 × 3 açık kohort ve aile kotası aşılınca istek reddediliyor.
  - **`Validated` yazımı:** `Validated` durumunu sonuç kaydı dışında set etmenin her yolu reddediliyor; paper ve Testnet verisi doğrulama hesabına girmiyor; gücü yetersiz ya da protokolü eksik kohort `Validated` olamıyor; işlem sayısı yetersizse sonuç `Inconclusive`.
  - **Erken değerlendirme ve import:** enjekte edilmiş saatle reddediliyor (uygulama katmanı ve DB trigger'ı ayrı ayrı test edilir).
  - **`Invalid`:** yalnızca kanıtla veriliyor; yeniden değerlendirme yalnızca aynı manifesto, pencere ve digest ile yapılabiliyor ve sınırı var; tüm koşular audit'te.
  - **Roller:** `eval_diagnostic` ve `eval_prospective` birbirinin şemasını okuyamıyor; Research ve Trading rolleri holdout şemalarını okuyamıyor.
  - **Tekrar üretilebilirlik:** aynı girdiler aynı sonuç hash'ini üretiyor.
  - **Değiştirilemezlik:** kohort silinemiyor, geri çekilemiyor; operasyonel devre dışı bırakma sonucu değiştirmiyor.
  - **H7–H11, H15–H18** sentetik ortamda geçiyor.
  - **Kohort 0** (baseline'lar) dondurulup push edildi.
- **Not:** Gerçek holdout verisi bu milestone'da hiçbir yere indirilmez.

### M4 — ModelRouter, yeterlilik altyapısı ve Researcher (M)
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

### M5 — Prod VPS, holdout ortamı ve deterministik paper trading (L)
- **Ön koşul:** **B2** (VPS satın alma onayı).
- **Kapsam:**
  - VPS sertleştirme (yalnızca SSH, parolalı anahtar, ssh-agent'a yüklenmeyen anahtar).
  - Dört ağlı docker-compose; etkileşimli deploy (T19).
  - ops DB: `market.live_candles` (yalnızca Trading), `research.*` view'ları (`candles`, `revealed_cohorts`, `revealed_paper_summary`) ve tüm DB rolleri (architecture §6.2).
  - Holdout DB ve evaluator: kohort penceresine bağlı import ve erken import'u reddeden DB trigger'ı. Holdout verisini **yalnızca evaluator**, kaynaktan import eder.
  - Trading.Host: canlı veri, ledger, risk (`ExperimentalPairsMaxExposure` dahil), simüle execution, reconciliation, kill switch, audit, Ops API (açık kohortların PnL'i görünür; manifesto ve sonuç değiştirilemez).
  - Research.Host'un VPS'e taşınması.
  - Yedek ve geri yükleme.
  - Korelasyon izleme ve P1 için tarihsel etki analizi.
- **Kabul kriterleri:**
  - **H-ENV (H5, H6, H6b, H13, H14, H19–H24)** gerçek ortamda geçti; ardından `HoldoutEnvironmentVerified` kullanıcı oturumuyla set edildi ve audit'e yazıldı. Bu bayrak yalnızca `Validated` yazımını kapılar.
  - Research rolü canlı mumları ve açığa çıkmamış paper sonuçlarını okuyamıyor; `eval_*` rolleri ops DB'sine bağlanamıyor; erken import reddediliyor (gerçek ortamda).
  - Deneysel parite tavanı için risk tablosu testleri geçiyor (tavanın tam sınırı, kırpma, çıkışın serbestliği, yetkisiz değişikliğin reddi).
  - Operasyonel güvenlik eylemleri (devre dışı bırakma, kill switch, pozisyon kapatma) çalışıyor ve audit'e yazılıyor; kohort kayıtlarını değiştirmiyor.
  - Parity replay geçiyor; risk tablosunun her satırı test edildi; bayat veride yalnızca çıkışlar çalışıyor; restart tutarlı.
  - 7 gün kesintisiz paper çalışması; yedekten geri yükleme denendi.
  - P1 analiz raporu kullanıcıya sunuldu.

### M6 — Binance Testnet execution (M)
- **Kabul kriterleri:**
  - Fault injection altında duplicate emir sayısı 0 ve ledger reconcile ediliyor.
  - Testnet'te ≥ 20 emrin tam yaşam döngüsü tamamlandı.
  - Filtre ihlali yapan emir borsaya gitmeden reddediliyor.
  - Anahtar yalnızca Trading.Host'ta.
  - `PaperTrading → TestnetActive` geçişi yalnızca `Frozen` stratejiler için ve kullanıcı oturumuyla mümkün; **doğrulama gerektirmiyor** (ADR-0012). `Validated` olmayan stratejiler "deneysel" etiketi taşıyor.

### M7 — Kontrollü web araştırması (M)
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
  - Runbook'ta bant dışı API anahtarı iptali dahil.
  - 30 günlük Testnet provası tamamlandı.

## Global "tamamlandı" tanımı
- Doğrulama script'i yeşil.
- Yeni kararlar ADR'de ve CHANGELOG'da.
- Kritik işlemler audit kaydı üretiyor.
- Repoda, logda ya da prompt'ta secret yok.
- `sources.md` güncel.
- "Uygulandı" denilen her kontrolün testi var.
