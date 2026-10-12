# Risk Modeli

Son güncelleme: 2026-10-13 (rev. 3) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

> Durum: Buradaki kontroller **planlanmıştır, uygulanmamıştır**. Etiketler (U/T/P/TBD) için bkz. `requirements.md`.

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
| Minimum emir | Exchange `NOTIONAL`/`MIN_NOTIONAL` değeri ve fee'ye göre anlamsız küçük emirlerin engellenmesi | T (exchange kuralı) |
| Emir tipleri | Limit ve koruma bantlı market | T |
| Korelasyon | **Bağlayıcı değil.** Korelasyon kümeleri ve etkin bağımsız pozisyon sayısı her döngüde hesaplanır, raporlanır ve eşik aşımında uyarı üretilir (uyarı eşiği TBD, M5'teki analizle; yalnızca bilgilendirme amaçlı). Bağlayıcı bir küme tavanı **P1** olarak bekliyor (M5'te tarihsel etki analiziyle önerilecek). Önceki onaysız %40 değeri kaldırıldı | T12 / P1 |

## 3. Kill switch'ler
### 3.1 Trading kill switch (D11)
| Tetik | Davranış |
|---|---|
| Manuel | Yeni emir açılmaz, açık emirler iptal edilir, pozisyonlar **tutulur**. "Tümünü kapat" ayrı bir manuel eylemdir |
| Drawdown limiti | Manuel ile aynı. Yeniden başlatma manuel |
| HTTP 418 | Otomatik halt ve alarm |
| Reconciliation tutarsızlığı | Yeni giriş açılmaz, alarm, manuel inceleme |

### 3.2 LLM acil durdurma (T9, ADR-0010)
`AiKillSwitch` trading kill switch'inden **bağımsızdır.** Ayrıntılar: `architecture.md` §3.4.

| Tetik | Davranış |
|---|---|
| Manuel (Ops API veya CLI) | Yeni LLM çağrısı yapılmaz, uçuştaki çağrılar ve Batch işleri iptal edilir, görevler `onUnavailable`'a düşer |
| Aylık tavana ulaşma | Aynı |
| Son 60 dakikadaki harcamanın aylık tavanın %20'sini aşması (başlangıç değeri) | Aynı |
| Art arda 3 çağrıda gerçek maliyetin tahmini 2 katını aşması (başlangıç değeri) | Aynı |
| Bant dışı | Google konsolunda API anahtarı iptali (runbook) |

LLM'in durdurulması trading'i durdurmaz. Operasyon varsayılan deterministik politikayla (T11) devam eder.

## 4. Hata senaryoları
| Senaryo | Davranış |
|---|---|
| Bayat veya eksik veri | Yeni giriş açılmaz, çıkışlar çalışır. Boşluk doldurulamazsa alarm |
| Emirde timeout veya 5xx | Emir `Unknown` durumuna geçer ve `origClientOrderId` ile çözülür. Körü körüne tekrar gönderilmez |
| 429 | Weight takibi, istemci tarafında limitleme, `Retry-After` |
| Kısmi dolum | Ledger dolan miktarla güncellenir. Kalan kısım config'teki süre sonunda iptal edilir |
| Duplicate gönderim | Kalıcı `clientOrderId` ile engellenir |
| Restart | Önce reconciliation. İşler idempotent |
| **LLM hatası, geçersiz çıktı, nitelikli model yok, bütçe aşımı ya da LLM acil durdurma** | **Varsayılan deterministik allocation politikası** (T11, ADR-0011): onaylı tavanlar içinde ters volatilite ağırlıklandırma. Backtest'te ve paper'da doğrulanmış olan aynı politika. Olay audit'e yazılır. Önceki onaysız "tavanların yarısı" kuralı kaldırıldı |
| Research'te LLM yok | Grid/parametrik arama ve insan onaylı hipotezler. Danışma amaçlı metinler yerine şablon kullanılır |
| Web kaynağına erişilemiyor (M7) | Önbellek kullanılır; yoksa iş atlanır |
| Evaluator'a erişilemiyor ya da `HoldoutEnvironmentVerified = false` | Adaylar `Candidate` durumunda bekler. Hiçbir aday holdout'u atlayamaz |

## 5. Araştırma ve sistem riskleri
| Risk | Önlem | Kalan risk |
|---|---|---|
| Overfitting ve data snooping | Walk-forward, Deflated Sharpe, yapı hash'iyle hesaplanan `TrialFamily`, holdout kotası ve Holm düzeltmesi | Sınırlı veri; paper ile doğrulama gerekir |
| Holdout kontaminasyonu | `architecture.md` §6.2'deki katmanlı kontroller (veri, yetki, ağ, yönetim erişimi, süreç, audit) | Kamuya açık veri, internetten doğrudan indirilebilir. Süreç kuralı ve audit ile ele alınır |
| Tekrarlı değerlendirmeyle holdout'a uydurma | Aile başına bir değerlendirme, dönem kotası K, yuvarlanmış özet, yeni evaluator sürümüyle tekrar değerlendirme yasağı | Kota içindeki yapısal varyantlar. Holm düzeltmesi bunu sınırlar |
| Kötü niyetli ya da hatalı bir evaluator sürümü | Sürümler (`EvaluatorRelease`) kaydedilir; deploy etkileşimli yapılır; evaluator kodunda değişiklik kontrol listesinde vurgulanır; her değişiklik `HoldoutEnvironmentVerified`'ı sıfırlar | İnsan incelemesine bağlı |
| Prompt injection (M7) | Tool'suz Extractor, şema, alıntı doğrulaması | — |
| Nitelikli olmayan modelin kullanılması | Görev politikası; downgrade yasağı test edilir | — |
| AI maliyetinin kontrolden çıkması | Rezervasyon, çok katmanlı tavanlar, LLM acil durdurma, aylık mutabakat | Geliştirme makinesindeki API anahtarının kodu atlayarak kullanılması. Google tarafındaki limitler ve mutabakat ile yakalanır |

## 6. Finansal ve operasyonel uyarılar
- Gerçek para kullanılmaz; kullanılması ayrı bir karar ve ayrı bir risk değerlendirmesi gerektirir.
- Testnet sonuçları performans kanıtı değildir.
- Gemini şartlarına göre hizmet finansal tavsiye için kullanılmamalıdır (S-G5). LLM çıktısı tavsiye olarak değil, deterministik kapılarla sınırlanmış bir girdi olarak kullanılır.
