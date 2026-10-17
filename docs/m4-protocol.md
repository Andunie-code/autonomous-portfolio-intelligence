# M4 Protokolü — LLM'siz araştırma ön kaydı (v1)

- Durum: **Ön kayıt — kullanıcı tarafından onaylandı (Kapı A, 2026-10-18, U).** Ön kayıt kanıtı, bu belgenin ve `m4-hypotheses.v1.json`'ın eklendiği git commit'idir. O commit'ten önce bu hipotezlerle araştırma penceresi `R` üzerinde hiçbir backtest, kapı değerlendirmesi ya da parametre denemesi **çalıştırılmadı.** Tanımlar yalnızca mevcut `DslParser` ve `DslValidator` ile (veri kullanılmadan) doğrulandı ve hash'lendi.
- Karar türleri: **U** = kullanıcı, **T** = teknik lider.
- İlgili: `m3-protocol.md` v1 ve `m3-protocol-v2.md` (bu belgede değiştirilmeyen her kural aynen geçerlidir), `m3-calibration-results-v2.md`, ADR-0006, ADR-0016, ADR-0017, `architecture.md` §3–§4 ve §6.6, `testing-strategy.md` §6.
- Amaç: roadmap M4 kabul kriteri **"LLM'siz modda en az 10 hipotez uçtan uca işlendi"** (boru hattı kriteri; istatistiksel değil). Bu koşu, nitelikli bir LLM olmadığında kullanılan varsayılan araştırma yoludur (architecture §3.2: grid ve insan onaylı hipotezler).

## 1. Kullanıcı kararları (U, 2026-10-18)
- **Deneme bütçesi:** M4'teki LLM'siz koşu, global deneme sayacını (şu an 2: M2 baseline'ları) **en fazla 100**'e çıkarabilir. Böylece Deflated Sharpe'taki `N`, kapı eşiklerinin kalibre edildiği `N = 100`'ü aşmaz.
- **Hipotez kaynağı:** hipotezleri Claude Code önerir, kullanıcı onaylar. Köken etiketleri ve kontaminasyon kaydı için §3'e bakın.

## 2. Hipotezler (T: içerik; U: onay)
Makinece okunur ve bağlayıcı liste: **`docs/m4-hypotheses.v1.json`**. Hipotez başına alanlar: kimlik, yapı hash'i ve grid noktaları; her nokta için tam DSL v1 tanımı ve tanım hash'i.
- **Liste hash'i:** 96 tanım hash'inin küçük harfli hex olarak sıralanıp `\n` ile birleştirilmesinin (sonda satır sonu yok) UTF-8 SHA-256'sı:
  **`89ab3965a8cbaf068788a95bc354f0340c29ae1b8d3834ef6c840bad1d4c2ec7`**. Bu değer dosyanın satır sonlarından bağımsızdır. Kapı B kodu dosyayı yükler; yapı ve liste hash'ini yeniden hesaplar, uyuşmazlıkta koşuyu başlatmaz.
- **Ortak ayarlar:** tüm tanımlar 4h aralığında, DSL v1 (ADR-0016) ve D27 havuzuyla yazıldı. 12 yapı hash'inin her biri tekildir ve iki M2 baseline'ının yapısından farklıdır. Hipotez içindeki grid noktaları aynı yapıyı paylaşır, dolayısıyla aynı aileye düşer (m3 §8).
- **Gerekçe:** hipotezler 2022'den önce yayımlanmış literatürden ve yaygın uygulayıcı kurallarından seçildi. Grid değerleri bu kaynaklardaki standart değerlerdir.

| # | Hipotez | İskelet (giriş / kapanış / stop) | Grid | Nokta | Yapı hash'i | Kaynak |
|---|---|---|---|---|---|---|
| H01 | Donchian kırılımı | `close > donchian(n).upper` / `close < donchian(⌊n/2⌋).lower` / ATR(14) × m | n ∈ {20, 55, 100} × m ∈ {2, 3, 4} | 9 | `ec9b1fe10bb3` | Donchian; "Turtle" kuralları (Faith 2003) |
| H02 | Bollinger ortalamaya dönüş | `close < bb.lower` / `close > bb.middle` / %6 | period ∈ {20, 40, 60} × k ∈ {1,5; 2; 2,5} | 9 | `7582e6d7e7c3` | Bollinger (2001) |
| H03 | Trend içinde RSI ortalamaya dönüş | `rsi(r) < t` ve `close > sma(200)` / `rsi > 50` / %8 | r ∈ {5, 10, 14} × t ∈ {25, 30, 35} | 9 | `86b80a293df8` | Connors ve Alvarez (2009) |
| H04 | Sıfır çizgisi üstünde MACD kesişimi | `cross_above(line, signal)` ve `line > 0` / `cross_below(line, signal)` / ATR × 3 | (fast, slow) ∈ {(8, 21), (12, 26), (19, 39)} × signal ∈ {6, 9, 12} | 9 | `3f28291e2f95` | Appel (1979) |
| H05 | ADX filtreli trend | `adx(14) > t`, `+DI > −DI`, `close > ema(e)` / `cross_below(+DI, −DI)` / ATR × 3 | t ∈ {20, 25, 30} × e ∈ {50, 100, 200} | 9 | `d5b82e1807fc` | Wilder (1978) |
| H06 | Zaman serisi momentumu | `cross_above(roc(n), 0)` / `cross_below(roc(n), 0)` / ATR × m | n ∈ {42, 90, 180} (7, 15, 30 gün) × m ∈ {2, 3, 4} | 9 | `116370c16905` | Moskowitz, Ooi ve Pedersen (2012) |
| H07 | Hacim teyitli kırılım | `close > donchian(n).upper` ve `volume > v × volume_sma(20)` / `close < ema(20)` / ATR × 2,5 | n ∈ {20, 50} × v ∈ {1,5; 2; 2,5} | 6 | `c748433bded8` | Blume, Easley ve O'Hara (1994) |
| H08 | BTC rejim filtreli SMA trendi | `cross_above(close, sma(n))` ve `BTC close > BTC sma(b)` / `cross_below(close, sma(n))` / ATR × 3 | n ∈ {50, 100, 150} × b ∈ {100, 200, 300} | 9 | `069d64c8cb20` | Faber (2007); ADR-0016 rejim filtresi |
| H09 | Bollinger sıkışma kırılımı | `bb(20, k).bandwidth[t−1] < w` ve `close > upper` / `close < middle` / ATR × 2,5 | w ∈ {0,08; 0,12; 0,16} × k ∈ {1,5; 2} | 6 | `00e92516c59a` | Bollinger (2001), "squeeze" |
| H10 | EMA trendinde geri çekilme sonrası giriş | `ema(f) > ema(s)` ve `cross_above(close, ema(f))` / `close < ema(s)` / trailing `ema(s)` | f ∈ {10, 20, 30} × s ∈ {80, 120, 200} | 9 | `8deadd9d5072` | Uygulayıcı trend takibi kuralı |
| H11 | RSI momentumu | `cross_above(rsi(r), t)` / `cross_below(rsi(r), 45)` / ATR × 3 | r ∈ {14, 21} × t ∈ {55, 60, 65} | 6 | `21ccbdd5c7d3` | Wilder (1978); momentum yorumu |
| H12 | BTC rejim filtreli MACD histogramı | `cross_above(hist, 0)` ve `BTC close > BTC ema(b)` / `cross_below(hist, 0)` / ATR × 3 | (fast, slow) ∈ {(8, 21), (12, 26), (19, 39)}, signal 9 × b ∈ {100, 200} | 6 | `873b68c6fbfe` | Appel (1979); ADR-0016 rejim filtresi |

**Toplam: 12 hipotez, 96 grid noktası (96 deneme).** Tüm indikatör girdileri `close`'tur; hacim karşılaştırmasında iki taraf da `QuoteVolume` kullanır. ATR stop'ları sabittir (`trailing: false`). D25 hard stop'u (%10) her tanımda ayrıca geçerlidir. En uzun ısınma, BTC `sma(300)` için 300 mumdur; veri `RankingWindow` başından (2021-07-05) okunduğu için ısınma `U`'dan önce tamamlanır.

## 3. Köken, bilgi kesimi ve kontaminasyon (U: kaynak; T: kayıt)
- **Köken:** hipotez kaydının (yapı) kökeni `dev-assisted`, grid noktalarından oluşan denemelerin kökeni `grid` (T18; deneme sayacı kökenden bağımsızdır).
- **Bilgi kesimi:** hipotezi öneren model Claude Opus 5.5'tir. Bilgi kesimi 2026-06 (S-A1). Bu tarih:
  - araştırma penceresini `R = [2022-01-01, 2026-01-01)` tümüyle kapsar. Hipotezlerin bu dönemin bilinen piyasa davranışından etkilenmemiş olduğu **garanti edilemez.** Etkiyi azaltmak için hipotezler ve grid değerleri 2022 öncesi literatürden alındı; etki yine de sıfır değildir;
  - tanısal holdout `[C, A_c)` ile çakışır. Hipotez kaydına `knowledge_cutoff = 2026-06-30` ve `knowledge_overlaps_diagnostic = true` yazılır. Bu bayrak, ileride yapılacak tanısal değerlendirme sonuçlarında gösterilir (architecture §6.5).
- Açığa çıkmamış kohortların paper, Testnet ya da tanısal sonuçları hipotez girdisi olarak kullanılmadı (CLAUDE.md).

## 4. Deneme muhasebesi (T)
- Her grid noktası, `R` üzerinde yeni bir tanım hash'i olarak **bir denemedir** (m3 §8). Benzerlik kontrolüyle mevcut bir aileye atanan noktalar da deneme sayılır.
- **Global sayaç:** koşudan önce 2, koşudan sonra **98** (≤ 100, §1).
- **G6'daki `N`:** tüm M4 değerlendirmelerinde **N = 98** (koşu sonundaki planlanan sayaç değeri). Bu değer koşu sırasından bağımsızdır ve m3 §6'daki "bu deneme dahil" kuralından daha sıkıdır.
- **Deneme sayılmayanlar:** G4 komşuları ve G3'ün katman içi seçim hesapları kapı hesaplamasıdır, yeni hipotez değildir; bu nedenle deneme sayılmaz ve `research.trials`'a yazılmaz (kalibrasyondaki uygulamayla aynı).
- Bir grid noktası koşu sırasında teknik bir hatayla çökerse deneme yine kaydedilir ve nokta sonuçta "hata" olarak raporlanır; yerine başka bir nokta konmaz.

## 5. Koşu (T)
- **Veri:** çekirdek evren `core-4y-S` (20 parite, snapshot `a6c4e89b…`), 4h. Mumlar `[RankingWindow.Start, C)` aralığından okunur; işlem `[U, C)` içinde yapılır. Referans seri BTCUSDT'dir. Yalnızca yerel dev DB kullanılır; ağ erişimi ve `≥ C` verisi yoktur.
- **Modeller:** maliyet modeli v1, allocation v1, `RiskSettings.NoHalt` (v2 §1: kapılar `no-halt` koşusunda), başlangıç sermayesi 10.000 USDT.
- **Seçim:**
  - Her grid noktası için (toplam 96) `R` üzerinde bir tam `no-halt` koşusu yapılır.
  - **Aday:** `M_R`'si (birincil metrik, m3 §4) en yüksek nokta. Eşitlikte dosyada önce gelen seçilir.
- **Betimleyici koşular:** seçilen adayın `policy` koşusu ile BTC ve sepet benchmark karşılaştırmaları raporlanır; kapı değildir.
- **Bootstrap:** `B = 10.000`, seed 20260101 (`ResearchGateEvaluator` varsayılanı, kalibrasyonla aynı).
- **Yol:** her hipotez Researcher tool'ları üzerinden işlenir: `ValidateStrategyDefinition` → `SubmitExperiment` (`ExperimentRequestGuard`, deneme kaydı) → `GetExperimentResult`. Kaynak, LLM'siz `ApprovedHypothesisSource` + `GridExpansion`'dır (ADR-0017). Sıra: H01 → H12, noktalar dosyadaki sırayla.

## 6. Kapılar (T; eşikler v2 kalibrasyonundan)
Seçilen aday için, `no-halt` koşusunda: `N_min = 100`, `τ_wf = 0,75`, `τ_nb = 0,80`, `τ_dsr = 0,90`.

| # | Kural |
|---|---|
| G1 | `M_R > 0` |
| G2 | İşlem sayısı ≥ 100 ve tek yönlü %95 bootstrap alt sınırı > 0 |
| G3 | **Ankore genişleyen pencere** (m3 §6, M4 grid kuralı): her test yılı `k` ∈ {2023, 2024, 2025} için önce nokta seçilir, sonra test edilir (ayrıntı aşağıda); pozitif test yıllarının oranı ≥ 0,75 olmalıdır |
| G4 | Seçilen adayın parametre komşuluğu (±%10): `M_R > 0` olan komşuların oranı ≥ 0,80. Raporlama için her adayda hesaplanır |
| G5 | m3 §6 (sabit kural) |
| G6 | DSR olasılığı ≥ 0,90; `N = 98`, `V = 1/(T − 1)` (v2 §2) |

**G3'ün hesaplanması:**
- **Seçim:** `[U, k)` içindeki 4h getirilerinin yıllıklandırılmış ortalaması en yüksek olan grid noktası. Eşitlikte dosyada önce gelen nokta seçilir.
- **Test:** seçilen noktanın `k` yılındaki yıllıklandırılmış ortalama log getirisi. Yıllar, mum açılış zamanına göre m3'teki gibi atanır.
- **Veri:** getiriler her noktanın tek tam koşusunun equity serisinden alınır. Seçim yalnızca `k`'den önceki veriye bakar.
- **Not:** 3 katmanla `τ_wf = 0,75` fiilen 3/3 gerektirir. 2022 test yılı değildir, çünkü seçim için önünde veri yoktur.

**Sonuç kategorileri:**
- **Aday:** tüm kapıları geçti.
- **Reddedildi:** en az bir kapıyı geçemedi; geçilemeyen kapılar listelenir.
- **Hata:** §4.

Bir **aday** hiçbir otomatik geçiş açmaz:
- kohort dondurma, paper'a giriş ve tanısal holdout M4 kapsamı dışındadır (tanısal holdout M5'te);
- her biri ayrı kullanıcı kararı gerektirir;
- aile kotası ve kohort sınırları (D21) o zaman uygulanır.

## 7. Koşu kuralı ve sapmalar (T)
- **Tek koşu:** koşu bir kez yapılır; sonuç ne olursa olsun `docs/m4-llmless-results.md`'ye yazılır. Hiçbir kapı geçilemese de bu kabul kriterini bozmaz.
- **Varyant yasağı:** sonuçlar görüldükten sonra hipotez, grid ya da kapı kuralı değiştirilmez. Bir varyant, yeni bir protokol sürümü ve yeni denemeler demektir.
- **Engine hatası:** koşudan sonra sonucu etkileyen bir engine hatası bulunursa düzeltilir ve aynı tanımlarla yeniden koşulur. Aynı tanım hash'leri yeni deneme değildir. Sapma, iki sonuç hash'iyle birlikte sonuç belgesine ve audit'e yazılır.
- **Tekrar üretilebilirlik:** her koşu snapshot hash'i, tanım hash'i, liste hash'i, protokol sürümü ve commit'i, engine git SHA'sı, maliyet/allocation/risk sürümleri, bootstrap seed'i ve sonuç hash'iyle kaydedilir. Aynı girdiler aynı sonuç hash'ini üretmelidir.

## 8. HypothesisGeneration yeterlilik çerçevesi (T; ayrıntılar Kapı C'de ayrı ön kayıtla)
Bu bölüm bir çerçevedir. Bağlayıcı değerler, Kapı C'de pilot sonrası yazılacak `m4-hg-qualification-protocol-v1.md`'de belirlenir.
- **Görev:** HypothesisGeneration, karar etkileyen bir görevdir (T7). Nitelikli model yokken `onUnavailable = DeterministicFallback`: LLM çağrısı yapılmaz, bu belgedeki LLM'siz yol kullanılır.
- **Setler:** pilot ve yeterlilik setleri ayrıktır (farklı seed'ler, farklı istem bağlamları ya da farklı parite alt kümeleri). Harness, iki setin kesişimi boş değilse çalışmaz.
- **Birincil metrik:** deneme başına tüm kapıları geçen aday oranı. LLM kolu, **aynı deneme sayısı ve aynı kapılarla** üretilen grid veya rastgele DSL baseline'ıyla karşılaştırılır; test tek yönlü Fisher exact testidir.
- **İkincil metrikler (betimleyici):** geçerli DSL oranı, tekrar ya da önemsiz hipotez oranı, deneme başına maliyet, gerekçe tutarlılığı (manuel örneklem).
- **Güvenlik değişmezleri** (tolerans 0, testing §6.1):
  - izin verilmeyen tool çağrısı;
  - yetki ya da bütçe sınırını aşma girişiminin etkili olması;
  - şema dışı çıktının kabul edilmesi;
  - uydurma `evidenceRef`'in validator'dan geçmesi.
- **Bütçe:** iş başına tavan, aylık tavanın %30'u (başlangıçta 3 USD). Gerekli örneklem bütçeye sığmazsa ya da güç yetersizse sonuç `Inconclusive` olur ve nitelik verilmez (ADR-0010).
- **Kapı C'de karara bağlanacak açık nokta:** yeterlilik denemeleri `R` üzerinde yapılırsa global sayaca girer ve `N`'yi 100'ün üstüne çıkarır (T18). Bu, sonraki adayların DSR eşiğini sıkılaştırır. Alternatif, sentetik evrenlerde yapılan bir yeterlilik setidir. Seçim, gerekçesiyle birlikte pilot sonrası protokolde yapılır ve kullanıcı onayına sunulur.
- **Bilgi kontaminasyonu:** Gemini modelinin bilgi kesimi `R`'yi kapsıyorsa LLM kolunun avantajı geçmiş bilgisinden gelebilir. Protokol bunu ele alır; örneğin baseline'la aynı pencere ve sentetik bir kontrol kolu kullanılabilir.
