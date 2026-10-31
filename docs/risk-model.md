# Risk Modeli

Son güncelleme: 2026-10-20 (rev. 5.1; M5, D29) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

> Durum: Paper için M5'te **uygulandı ve test edildi** (`m5-protocol.md` §8, CHANGELOG 2026-10-20): §2'deki limitler, koruyucu stop, deneysel parite tavanı, küme tavanı, veri tazeliği ve trading kill switch. Testnet'e özgü kontroller (borsadaki stop emri, emir hataları) M6'da (`m6-protocol.md`, ADR-0021; Kapı A onaylandı 2026-10-26). Etiketler (U/T/P/TBD) için bkz. `requirements.md`.

## 1. İlkeler
- Risk motoru deterministiktir ve AI'dan bağımsızdır. LLM `RiskConfig`'i okuyamaz ve yazamaz; yalnızca kalan limitlerin özetini görür.
- `RiskConfig` versiyonludur. Yalnızca kimliği doğrulanmış kullanıcı değiştirir ve her değişiklik audit'e yazılır.
- Belirsizlikte yeni giriş açılmaz. Çıkış ve stop sinyallerini LLM hiçbir koşulda engelleyemez.
- Teknik lider hiçbir risk limitini gevşetemez ya da yenisini bağlayıcı hale getiremez. Bunlar kullanıcı kararıdır (P1).

## 2. Limitler
| Limit | Değer | Durum |
|---|---|---|
| Maksimum açık pozisyon | 5 | U (D7) |
| Varlık başına maruziyet | Sermayenin %20'si | U (D7) |
| Toplam maruziyet | Sermayenin %80'i | U (D7) |
| Günlük kayıp | %3. Aşılırsa 24 saat boyunca yeni giriş yok | U (D7) |
| Maksimum drawdown | Zirveden %15. Aşılırsa otomatik halt, yeniden başlatma manuel | U (D7) |
| Veri tazeliği | Son mum, beklenenden 2 mumdan fazla gecikmişse yeni giriş yok | U (D7) |
| **Koruyucu stop (pozisyon başına hard stop)** | Her pozisyon girişte bir koruyucu stop seviyesi alır. Efektif seviye, strateji stop'u ile `RiskConfig`'teki "pozisyon başına maksimum kayıp" seviyesinden girişe en yakın olanıdır. Hard stop değeri: giriş fiyatından **%10** (D25, 2026-10-14). Mum içinde tetiklenir; backtest ve paper'da OHLC kuralıyla, Testnet'te borsadaki stop emriyle uygulanır (ADR-0014). Günlük kayıp ve drawdown limitleri mum kapanışında değerlendirilir | U (D25, 2026-10-14) |
| Minimum emir | Exchange `NOTIONAL`/`MIN_NOTIONAL` değeri ve fee'ye göre anlamsız küçük emirlerin engellenmesi | T (exchange kuralı) |
| Emir tipleri | Testnet:<br>- giriş: koruma bantlı `LIMIT IOC` (referans fiyatın +%1'i);<br>- sinyal çıkışı ve kapatma: `MARKET`;<br>- koruyucu stop: `STOP_LOSS` (tetiklenince market); sembolde yoksa bantlı `STOP_LOSS_LIMIT` (S-B14, S-B18).<br>Filtre ihlali yapan emir borsaya gitmez | U (U-M6-5, 2026-10-26) |
| **Deneysel parite toplam maruziyeti** (`ExperimentalPairsMaxExposure`) | Portföy değerinin **%10'u** (E1 ve E2 paritelerinin toplamı). Mevcut limitlere **ek olarak** uygulanır; en kısıtlayıcı limit geçerlidir. Tavanı aşacak alım kırpılır ya da reddedilir. Fiyat hareketiyle aşılırsa zorunlu satış yapılmaz, yalnızca yeni deneysel girişler durur. Çıkışlar her zaman serbesttir. Yalnızca kullanıcı, kimliği doğrulanmış Ops API üzerinden ve versiyonlu `RiskConfig` ile değiştirebilir; LLM, Research ve evaluator erişemez | U (U5, 2026-10-13) |
| **Korelasyon kümesi tavanı** | Korelasyon kümesi başına toplam maruziyet portföy değerinin **%60'ı**. Kümeler; son 30 günlük günlük log getirileri, Pearson korelasyonu, ortalama bağlantılı hiyerarşik kümeleme ve **ρ* = 0,70** kesimiyle, yalnızca o güne kadarki veriden oluşturulur. Deneysel tavanla aynı kural uygulanır: aşan alım kırpılır, kırpılmış tutar `MIN_NOTIONAL`'ın altındaysa reddedilir; fiyat hareketiyle aşımda zorunlu satış yapılmaz; çıkışlar serbesttir. Etkin bağımsız pozisyon sayısı her döngüde hesaplanır ve raporlanır. En büyük küme payı **%54**'ü aşınca uyarı üretilir (audit `correlation_warning`). Operasyon `RiskConfig`'indedir; araştırma backtest'lerine ve kohort manifestolarına uygulanmaz. Dayanak: `m5-p1-analysis.md` (tarihsel kanıt ince) | U (D29, 2026-10-20) |

## 3. Kill switch'ler
### 3.1 Trading kill switch (D11, rev. 2026-10-14)
| Tetik | Davranış |
|---|---|
| Manuel | Yeni emir açılmaz, açık **giriş** emirleri iptal edilir, pozisyonlar **tutulur**. **Koruyucu stop emirleri iptal edilmez** (çıkışlar her zaman çalışır, ADR-0014). "Tümünü kapat" ayrı bir manuel eylemdir |
| Drawdown limiti | Manuel ile aynı. Yeniden başlatma manuel |
| HTTP 418 | Otomatik halt ve alarm |
| Reconciliation tutarsızlığı | Yeni giriş açılmaz, alarm, manuel inceleme |

### 3.2 LLM acil durdurma (T9, ADR-0010)
`AiKillSwitch` trading kill switch'inden **bağımsızdır.** Ayrıntılar: `architecture.md` §3.4.

| Tetik | Davranış |
|---|---|
| Manuel (CLI: `m4-ai.ps1 killswitch trip`; kapatma da yalnızca CLI) | Yeni LLM çağrısı yapılmaz, uçuştaki çağrılar ve Batch işleri iptal edilir, görevler `onUnavailable`'a düşer |
| Aylık tavana ulaşma | Aynı |
| Son 60 dakikadaki harcamanın aylık tavanın %20'sini aşması (başlangıç değeri) | Aynı |
| Art arda 3 çağrıda gerçek maliyetin tahmini 2 katını aşması (başlangıç değeri) | Aynı |
| Bant dışı | Google konsolunda API anahtarı iptali (`runbook.md` §2) |

LLM'in durdurulması trading'i durdurmaz. Operasyon varsayılan deterministik politikayla (T11) devam eder.

## 4. Hata senaryoları
| Senaryo | Davranış |
|---|---|
| Bayat veya eksik veri | Yeni giriş açılmaz, çıkışlar çalışır. Boşluk doldurulamazsa alarm. Paper'da stop kuralı veri geldiğinde kapanan mumlara uygulanır; Testnet'te stop emri borsada bekler |
| Koruyucu stop emri reddedildi ya da dolmadı (Testnet) | Pozisyon "korumasız" olarak işaretlenir ve alarm üretilir. Testnet run'ında yeni giriş durur; stop her döngüde yeniden denenir. Korumasız pozisyonun kapanan mumunda `Low ≤ S` ise `MARKET` çıkış gönderilir. Sapma fark raporuna girer (ADR-0014, ADR-0015, ADR-0021) |
| Makine kapalı, uykuda ya da yeniden başlatıldı (ADR-0013) | Paper döngüsü durur; açılışta önce reconciliation, sonra kaçırılan kapanmış mumlar sırayla işlenir (stop kuralı dahil). Testnet'te stop emirleri borsada kalır. İleriye dönük değerlendirme arşiv verisi kullandığı için etkilenmez |
| Emirde timeout veya 5xx | Emir `Unknown` durumuna geçer ve `origClientOrderId` ile çözülür. Körü körüne tekrar gönderilmez. `timestamp + recvWindow + 60 sn` geçtikten sonra iki ardışık `-2013` gelirse emir `NeverAccepted` olur. Aynı niyet yalnızca bundan sonra yeni kimlikle gönderilebilir (S-B11, `m6-protocol.md` §3.3) |
| Testnet sıfırlaması (yaklaşık ayda bir, S-B19) | Reconciliation `testnet-reset-suspected` alarmı üretir; yeni giriş açılmaz. Kullanıcının onaylı "yeniden taban" eylemi yeni bir Testnet run'ı açar. Paper etkilenmez (`m6-protocol.md` §4.4) |
| 429 | Weight takibi, istemci tarafında limitleme, `Retry-After` |
| Kısmi dolum | Ledger dolan miktarla güncellenir. Kalan kısım config'teki süre sonunda iptal edilir |
| Duplicate gönderim | Kalıcı `clientOrderId` ile engellenir |
| Restart | Önce reconciliation. İşler idempotent |
| **LLM hatası, geçersiz çıktı, nitelikli model yok, bütçe aşımı ya da LLM acil durdurma** | **Varsayılan deterministik allocation politikası** (T11, ADR-0011): onaylı tavanlar içinde ters volatilite ağırlıklandırma. Backtest'te ve paper'da doğrulanmış olan aynı politika. Olay audit'e yazılır. Önceki onaysız "tavanların yarısı" kuralı kaldırıldı. M8: fallback nedenleri ve çıkışlara müdahale girişiminin güvenlik olayı sayılması `m8-protocol.md` §5.3–§5.4 |
| Research'te LLM yok | Grid/parametrik arama ve insan onaylı hipotezler. Danışma amaçlı metinler yerine şablon kullanılır |
| Web kaynağına erişilemiyor (M7) | Önbellek kullanılır; yoksa iş atlanır. `robots.txt` okunamazsa host yasak sayılır; arXiv'de yalnızca API özeti kullanılır. 403'ten sonra o host'a istek gönderilmez (ADR-0022) |
| Evaluator'a erişilemiyor ya da `HoldoutEnvironmentVerified = false` | Paper ve Testnet devam eder (ADR-0012). `Validated` sonucu yazılamaz; değerlendirme ortam doğrulandıktan sonra aynı girdilerle yapılır |
| Operasyonel güvenlik eylemi (devre dışı bırakma, kill switch, pozisyon kapatma) | Her zaman serbesttir ve audit'e yazılır. Doğrulama durumundan ve kohort yaşam döngüsünden bağımsızdır; kohort kayıtlarını değiştirmez |

## 5. Araştırma ve sistem riskleri
| Risk | Önlem | Kalan risk |
|---|---|---|
| Overfitting ve data snooping | Walk-forward, Deflated Sharpe, yapı hash'iyle hesaplanan `TrialFamily`, holdout kotası ve Holm düzeltmesi | Sınırlı veri; paper ile doğrulama gerekir |
| Holdout kontaminasyonu | `architecture.md` §6.2'deki katmanlı kontroller (rol ve `CONNECT` yetkileri, `pg_hba`, Docker ağları, süreç, audit) ve T17 | Kamuya açık veri, internetten doğrudan indirilebilir. **Holdout aynı makinede ve aynı PostgreSQL instance'ında (D17 rev., ADR-0013):** geliştirici ve Claude Code Docker yetkisiyle kontrolleri aşabilir. Kabul edilmiş artık risk; süreç kuralı, deny kuralları ve audit ile ele alınır |
| Tekrarlı değerlendirmeyle holdout'a uydurma | Tanısal: aile başına ömür boyu 1, kesim dönemi başına 10, yalnızca eleme (`NotRejected` / `Rejected`). İleriye dönük: her kohort yalnızca kendi penceresinde ve bir kez değerlendirilir; açığa çıkmış pencere tekrar kullanılamaz; aile başına 2 deneme; yeniden değerlendirme yalnızca aynı manifesto ve digest ile | Kota içindeki yapısal varyantlar. Global sınırlar ve tam raporlama bunu sınırlar |
| Kötü niyetli ya da hatalı bir evaluator sürümü | Sürümler (`EvaluatorRelease`) kaydedilir; deploy etkileşimli yapılır; evaluator kodunda değişiklik kontrol listesinde vurgulanır; her değişiklik `HoldoutEnvironmentVerified`'ı sıfırlar | İnsan incelemesine bağlı |
| Bilgi kontaminasyonu (tanısal holdout insanlar ve LLM'ler tarafından biliniyor olabilir) | Tanısal holdout yalnızca eler, terfi ettirmez. Model bilgi kesim tarihi her hipotezde kaydedilir ve işaretlenir | Claude'un eğitim kesim tarihi Haziran 2026; Gemini'ninki doğrulanmadı |
| İleriye dönük güç ve bekleme süresi | Ön kayıtlı güç analizi. Gücü yetersizse sonuç `Inconclusive`, asla `Validated` | İlk `Validated` en erken 6 ay sonra. Düşük frekanslı stratejiler büyük olasılıkla `Inconclusive` |
| Seçim yanlılığı (paralel kohortlar, varyantlar) | 2 × 3 açık kohort sınırı, aile kotası, kohort içinde Holm, "n denemeden k Validated" raporlaması | Aile kimliği atlatılabilir (hash ve benzerlik sınırları). Benzerlik yalnızca aile temsilcileriyle karşılaştırıldığı için temsilciden adım adım uzaklaşan varyantlar yakalanmayabilir (ADR-0015); global sınırlar asıl güvence |
| Deneysel pariteler (kısa geçmiş) | E1 ve E2 kategorileri, %10 maruziyet tavanı, kanıt düzeyi etiketleri, doğrulamanın kategoriler arasında taşınmaması | Hayatta kalma yanlılığı çekirdekte tasarım gereği var; raporlanır |
| Makine yöneticisi ya da saat manipülasyonu | Audit, kaynak dosyaların tarih başlıkları | Kabul edilmiş artık risk (yerel tek makine, ADR-0013) |
| Runtime anahtarlarının Claude Code ile aynı makinede olması | Process ayrımı (D9), Docker secret dosyaları, `Read(**/secrets/**)` deny kuralı, trade-only ve withdrawal'sız Testnet anahtarı; IP kısıtı yerine `TestnetGuardHandler` host allowlist'i (D33) | Kabul edilmiş artık risk (ADR-0013). Ağ düzeyinde egress kısıtı yok; sınır koddaki guard'dır (ADR-0021) |
| Bellek baskısı (16 GB, IDE ve Claude Code ile ortak) | Servis başına `mem_limit`/`cpus`, iş bazlı Research.Host ve Evaluator, ağır işlerin sırayla çalışması (ADR-0013) | Ağır iş sırasında Trading.Host'un gecikmesi; döngü süreleri metrik olarak izlenir |
| Prompt injection (M7) | Tool'suz Extractor, sınırlayıcılı girdi, şema; normalize metinde tam alt dizgi olarak alıntı doğrulaması; sayısal kontrol; sanitizer. Yeterlilikte injection seti ve sıfır toleranslı değişmezler. Bulgunun tek etkisi bir hipoteze kaynak olmaktır (ADR-0022) | Belgede düz metin olarak geçen injection içeriği doğrulanabilir bir alıntıdır; kod tek başına yakalayamaz. Dış CSS ya da üst üste binen nesnelerle gizlenmiş metin süzülmez |
| Kesim sonrası yayınla holdout bilgisinin sızması (M7) | Yalnızca `published` ve `updated` tarihi `< C` olan arXiv sürümleri kabul edilir. Kontrol istemcide, DB `CHECK` kısıtında ve hipotez kaydında tekrarlanır (U-M7-2) | Kesim öncesi bir sürümün sonradan yayımlanmış bilgiye atıf yapamayacağı varsayılır |
| Nitelikli olmayan modelin kullanılması | Görev politikası; downgrade yasağı test edilir | — |
| AI maliyetinin kontrolden çıkması | Rezervasyon, çok katmanlı tavanlar, LLM acil durdurma, aylık mutabakat | Aynı makinedeki API anahtarlarının kodu atlayarak kullanılması. Google tarafındaki limitler ve mutabakat ile yakalanır |

## 6. Finansal ve operasyonel uyarılar
- Gerçek para kullanılmaz; kullanılması ayrı bir karar ve ayrı bir risk değerlendirmesi gerektirir.
- Testnet sonuçları performans kanıtı değildir.
- Gemini şartlarına göre hizmet finansal tavsiye için kullanılmamalıdır (S-G5). LLM çıktısı tavsiye olarak değil, deterministik kapılarla sınırlanmış bir girdi olarak kullanılır.
