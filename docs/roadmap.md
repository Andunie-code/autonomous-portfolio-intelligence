# Yol Haritası

Son güncelleme: 2026-10-13 (rev. 3) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

> Durum: **M0 yerelde tamamlandı** (2026-10-13; doğrulama `716a1bd` commit'inde, temiz klonda). Uzak depo: private GitHub deposu `Andunie-code/autonomous-portfolio-intelligence`. M1 ve sonrası başlamadı; her milestone ayrı kullanıcı onayı gerektirir. Bir milestone, kabul kriterlerindeki testler **gerçekten uygulanıp geçtiğinde** tamamlanmış sayılır.

## Sıralamanın mantığı
1. Önce kanıt altyapısı (veri, backtest, değerlendirme), sonra para hareketi.
2. Sistem LLM olmadan tam işlevli olmalı. LLM görev sınıfları ancak nitelik kazanınca devreye girer.
3. Gerçek holdout ortamı, D5'teki VPS ile birlikte M5'te kurulur. O zamana kadar gerçek holdout değerlendirmesi yapılmaz (T3).
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

### M1 — Veri temeli (M)
- **Kapsam:**
  - Binance public data import (1h, 4h); checksum; ms/µs normalizasyonu; OHLC kontrolleri; boşluk tespiti ve REST ile doldurma; exchangeInfo; `DatasetSnapshot`.
  - **1. holdout dönemi:** `H_end` = M1 başlangıç tarihi, `H_start` = `H_end − 9 ay`; dondurulur ve kaydedilir (T4).
  - Pencere koruması: importçu, REST boşluk doldurucu, dosya listesi (sınır ayı günlük dosya, T6) ve dev DB `CHECK` kısıtı.
- **Kabul kriterleri:**
  - 20 parite için, pencere hariç ≥ 4 yıllık 1h ve 4h veri yüklü.
  - Bozuk checksum reddediliyor; 2025 ms/µs testi geçiyor; import idempotent; hash kararlı; boşluk raporu üretiliyor.
  - **H1, H2, H2b, H3, H4** (`testing-strategy.md` §7.1) geçiyor. dev DB'sinde pencere içinde mum sayısı 0. İndirme klasöründe pencereyle kesişen aylık zip yok.

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
  - **`TrialFamily` yapı hash'i** ve deneme sayacı.
  - Yaşam döngüsü (`Candidate → HoldoutTested`, `HoldoutEnvironmentVerified` bayrağına bağlı).
  - **HoldoutEvaluator servisi:** dar API, kota, aile kuralı, sürüm kontrolü, yuvarlanmış yanıt, audit. Yerelde ayrı bir container'da, ayrı kimlik bilgileriyle ve **yalnızca sentetik veriyle** çalışır.
  - Kapı eşiklerinin sentetik kalibrasyonu; kalibrasyon protokolü ön kayıtla.
- **Kabul kriterleri:**
  - DSL ve kodlanmış baseline parity'si sağlanıyor.
  - Kalibrasyon protokolü kaydedildi; random walk testinde yanlış pozitif oranı protokoldeki hedefin altında; yerleştirilmiş avantaj testi geçiyor.
  - `TrialFamily` hash testleri geçiyor.
  - **H7, H8, H9, H10, H11, H15, H16, H17, H18** sentetik ortamda geçiyor.
  - `HoldoutEnvironmentVerified = false` iken `Candidate → HoldoutTested` geçişi reddediliyor.
  - Yetkisiz yaşam döngüsü geçişleri reddediliyor.
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
  - ops DB (`research.candles` view'ı ve yetkiler); holdout DB ve evaluator; gerçek holdout verisinin **yalnızca evaluator tarafından** import edilmesi.
  - Trading.Host: canlı veri, ledger, risk, simüle execution, reconciliation, kill switch, audit, Ops API.
  - Research.Host'un VPS'e taşınması.
  - Yedek ve geri yükleme.
  - Korelasyon izleme ve P1 için tarihsel etki analizi.
- **Kabul kriterleri:**
  - **H-ENV (H5, H6, H6b, H13, H14, H19, H20)** gerçek ortamda geçti; ardından `HoldoutEnvironmentVerified` kullanıcı oturumuyla set edildi ve audit'e yazıldı.
  - 1. dönemin K ve α değerleri, açılıştan önce kayda geçirildi.
  - Parity replay geçiyor; risk tablosunun her satırı test edildi; bayat veride yalnızca çıkışlar çalışıyor; restart tutarlı.
  - 7 gün kesintisiz paper çalışması; yedekten geri yükleme denendi.
  - P1 analiz raporu kullanıcıya sunuldu.

### M6 — Binance Testnet execution (M)
- **Kabul kriterleri:**
  - Fault injection altında duplicate emir sayısı 0 ve ledger reconcile ediliyor.
  - Testnet'te ≥ 20 emrin tam yaşam döngüsü tamamlandı.
  - Filtre ihlali yapan emir borsaya gitmeden reddediliyor.
  - Anahtar yalnızca Trading.Host'ta.

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
