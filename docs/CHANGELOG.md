# Belge Değişiklik Kaydı

M0 ile birlikte git'e geçildi; bundan sonra ayrıntılı geçmiş git'te, karar düzeyindeki değişiklikler burada.

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
