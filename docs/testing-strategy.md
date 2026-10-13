# Test Stratejisi

Son güncelleme: 2026-10-13 (rev. 4) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

> Durum: Buradaki testler **planlanmıştır, yazılmamıştır.** Bir kontrol, testi uygulanıp geçmeden "uygulandı" olarak raporlanmaz.

## 1. Katmanlar
| Katman | Kapsam | Ne zaman çalışır |
|---|---|---|
| Unit ve property-based | İndikatörler, DSL, maliyet ve sizing, risk kuralları, durum makineleri, look-ahead, determinizm | Her doğrulama koşusu (yerel script, T16) |
| Mimari | Modül bağımlılık kuralları | Her koşu |
| Entegrasyon | PostgreSQL (Testcontainers), kuyruk, import, sahte exchange, sahte AI sağlayıcısı | Her koşu |
| Fault injection | Exchange ve AI hataları | Her koşu |
| **H-DEV** (holdout, geliştirme tarafı) | §7.1 | Her koşu |
| **H-ENV** (holdout, gerçek ortam) | §7.2. Gerçek VPS ortamına karşı | Ortam kurulduğunda, evaluator ya da altyapı her değiştiğinde, ayda bir. Sonuç `HoldoutEnvironmentVerified`'ı belirler |
| Testnet | Gerçek Testnet'e küçük emirler | Manuel veya gecelik |
| Model yeterliliği | §6. Ön kayıtlı, bütçeli | Protokole göre |

## 2. Veri doğruluğu (M1)
- Bozuk checksum reddedilir.
- 2025 sınırındaki ms/µs dönüşümü doğru ve sıralı.
- OHLC tutarlılığı.
- Boşluk tespiti.
- İdempotent import.
- Kararlı hash.

## 3. Kernel ve backtest (M2)
- Golden testler (referans değerlerle, tolerans config'te).
- Look-ahead property testi: t'den sonraki mumların değiştirilmesi, t'ye kadarki değerleri değiştirmez.
- Muhasebe invariant'ı.
- Bit bit determinizm.
- Benchmark'ların analitik değerlerle eşleşmesi.
- Maliyet modelinin negatif olmaması.
- **Varsayılan allocation politikası (T11):** ağırlıklar onaylı tavanları hiçbir zaman aşmaz (property testi); volatilite verisi eksikse varlık sıfır ağırlık alır; sonuç deterministik.

## 4. Değerlendirme hattı (M3)
- **Yanlış pozitif kalibrasyonu:** Random walk verisi üzerinde rastgele N strateji çalıştırılır; kapı eşikleri, ön kayıtlı hedef yanlış pozitif oranı sağlanacak şekilde kalibre edilir (hedef oran TBD, `requirements.md` §8).
- **Doğru pozitif:** İçine bilinen bir avantaj yerleştirilmiş sentetik veride, o avantajı kullanan strateji kapıları geçer.
- **DSL ve kodlanmış baseline parity'si.**
- **`TrialFamily` yapı hash'i:** Yalnızca parametre değişikliği aynı hash'i, yapısal değişiklik farklı hash'i üretir. JSON'da alan sırası ve boşluklar hash'i etkilemez.
- **Deneme sayacı:** LLM, grid ve insan menşeli denemelerin hepsi sayılır.

## 5. Operasyon (M5–M6)
- Parity: canlı motorla replay, backtest ile aynı sonucu verir.
- Risk tablosunun her satırı test edilir.
- Fault injection altında duplicate emir sayısı 0.
- Kanarya secret testi.
- Audit tablosunda `UPDATE`/`DELETE` reddedilir.

## 6. Model yeterliliği (D16, D20, T7, T8, ADR-0010)

### 6.1 Süreç
1. **Pilot:** Pilot sette (yeterlilik setinden **ayrı**) metriklerin varyansı ölçülür. Pilot sonuçları nitelik kazandırmaz.
2. **Protokol (ön kayıt):** Protokol belgesi şunları içerir:
   - görev sınıfı, model, prompt sürümü;
   - yeterlilik setinin hash'i;
   - birincil metrik ve karar kuralı;
   - α ve minimum anlamlı etki (pilot verisiyle gerekçelendirilir);
   - güç analiziyle hesaplanmış örneklem büyüklüğü;
   - bütçe (iş başına tavan);
   - güvenlik değişmezleri.

   Belge hash'lenir ve kaydedilir. Harness, protokol hash'i olmadan çalışmaz. Sonuçlar görüldükten sonra protokol değiştirilemez; değişiklik yeni bir protokol ve **yeni bir yeterlilik seti** demektir.
3. **Koşu:** Ücretli katmanda (T10) yapılır. Tüm çağrılar `DecisionTrace`'e yazılır.
4. **Kayıt:** `TaskQualification` (protokol hash'i, metrikler, maliyet, geçti/kaldı). "Kaldı" sonucu da kaydedilir.
5. **Yeniden yeterlilik:** Model, prompt, şema ya da set değişirse nitelik düşer.
6. **Karşılaştırma kolları:** Yetkili kollar Gemini modelleri ve deterministik baseline'dır. Başka bir sağlayıcı kolu ayrı onay gerektirir (D19, P3).

**Güvenlik değişmezleri** (tolerans 0, tek bir ihlal başarısızlıktır; istatistiksel eşik değildir):
- izin verilmeyen tool çağrısı;
- yetki ya da bütçe sınırını aşma girişiminin etkili olması;
- şema dışı çıktının kabul edilmesi;
- uydurma `evidenceRef`'in validator'dan geçmesi.

### 6.2 Görev sınıfları
| Görev sınıfı | Girdi ve set | Birincil metrik ve karar kuralı | İkincil metrikler | α, etki, örneklem | Başarısız olursa |
|---|---|---|---|---|---|
| **HypothesisGeneration** (M4) | Holdout dışı veri, deney geçmişi, beyaz liste. Baseline: aynı deneme sayısı ve kapılarla grid/rastgele DSL | Deneme başına kapıyı geçen aday oranı, baseline'la karşılaştırılır; tek yönlü Fisher exact testi | Geçerli DSL oranı; tekrar ya da önemsiz hipotez oranı; deneme başına maliyet; gerekçe tutarlılığı (manuel örneklem) | TBD (pilot ve güç analiziyle). Gerekli örneklem bütçeye sığmazsa sonuç "belirsiz" olur ve nitelik **verilmez** | LLM'siz araştırma (grid ve insan) |
| **FindingExtraction** (M7) | Etiketli belge seti ve injection seti | Alıntı doğrulama oranı (bir alt sınırla) | Uydurma alıntı oranı; bulgu kesinliği (etiketlere göre) | TBD | Web araştırması kapalı |
| **AllocationProposal** (M8) | İleriye dönük gölge mod, ≥ 4 hafta (U). Karşılaştırma varsayılan deterministik politikayla | Net getiri farkının güven aralığı. **Non-inferiority** (riski düşürmeden kötü olmama) ve drawdown'da kötüleşme olmaması. Güven aralığı belirsiz kalırsa **nitelik verilmez** | Clamp ve red oranı; turnover maliyeti; `EvidenceValidator` geçme oranı | Non-inferiority marjı, süre ve karar sayısı TBD (gölge modun ilk haftalarındaki varyansla). Look-ahead nedeniyle kesim tarihi öncesi replay yalnızca smoke test | Varsayılan deterministik politika kalıcı olur |
| ResultInterpretation, Summary | Danışma: yeterlilik yok | Her çıktı runtime'da şema ve `EvidenceValidator`'dan geçer; geçemeyen çıktı atılır | Atılma oranı izlenir | — | Deterministik rapor ya da şablon |

**Teknik spike (M4, yeterlilik değil):** structured output, function calling ve ikisinin birlikte kullanımı; nötr şemaların sığması; refusal davranışı. Sentetik içerikle, ücretsiz katman kullanılabilir. Başarısız olursa: şemalar sadeleştirilir ya da tool kullanılmadan yalnızca structured output ile çalışılır. Bu da olmazsa görev sınıfı nitelikli modelsiz kalır.

### 6.3 ModelRouter ve harcama kontrolü testleri (M4, sahte sağlayıcıyla, sıfır maliyet)
- Karar etkileyen bir görevde nitelikli olmayan bir modele yönlendirme yapılamaz (0 ihlal).
- Aylık, iş başına ve çağrı başına tavanlar: rezervasyon tavanı aşacaksa çağrı yapılmaz.
- `actualModel ≠ requestedModel` olduğunda sonuç degraded olur ve kapıdan geçemez.
- Refusal ve geçersiz çıktı `NoDecision` ile sonuçlanır.
- **LLM acil durdurma:**
  - bayrak açıkken çağrı yapılmaz;
  - uçuştaki çağrı iptal edilir;
  - Batch iptal çağrısı yapılır;
  - her otomatik tetik (tavan, harcama hızı, maliyet sapması, circuit breaker) bayrağı açar;
  - bayrak yalnızca manuel kapatılabilir ve audit'e yazılır.
- Retry politikası: 4xx'te retry yapılmaz; `Retry-After`'a uyulur; deneme sayısı sınırı aşılmaz.

### 6.4 Web araştırmasının değeri (M7)
Aynı bütçeyle web'li ve web'siz Researcher karşılaştırılır. Injection seti güvenlik değişmezlerine tabidir.

## 7. Holdout, kohort ve doğrulama testleri (D10, D17, D21–D23; ADR-0006, ADR-0009, ADR-0012)
Kesim tarihi `C = 2026-01-01T00:00:00Z`. Mum üyeliği: `S ≤ o` ve `o + Δ ≤ E`.

### 7.1 H-DEV (her koşu; çevrimdışı)
| # | Test | Beklenen | Milestone |
|---|---|---|---|
| H1 | Ortam kontrol script'i: dev DB'sinde `open_time + interval > C` olan satır sayısı | 0 | M1 |
| H1b | Üyelik kuralı: 2025-12-31T23:00Z açılışlı 1h mum dahil; 2026-01-01T00:00Z açılışlı 1h mum hariç; 2025-12-29 açılışlı 1w mumu hariç; 2025-12-31 açılışlı 1d mumu dahil | Doğru | M1 |
| H1c | UTC: ms ve µs aynı anı veriyor; offset 0; DB oturumu `Europe/Istanbul` iken `CHECK` doğru çalışıyor; UTC dışı makine saat diliminde testler geçiyor | Doğru | M1 |
| H1d | `C` migration'da ve kodda aynı | Eşit | M1 |
| H2 | Importçu ya da HTTP istemcisiyle `C` sonrasına uzanan istek | Ağa gitmeden reddedilir ve loglanır | M1 |
| H2b | Dosya planlayıcı (rastgele tarihlerle property testi) | `≥ C` aralığını kapsayan hiçbir dosya listelenmez | M1 |
| H2c | Host allowlist'i, redirect reddi; testler gerçek ağa çıkmıyor | Doğru | M1 |
| H3 | `market.candles`'a sınırdaki satırları (haftalık mum dahil) doğrudan ekleme | `CHECK` reddeder | M1 |
| H4 | Repo ve geliştirme secret/config taraması | Holdout ya da VPS secret'ı bulunmaz | M0/M1 |
| H7 | `GetDatasetCatalog` | `≥ C` verisini içermez | M3 |
| H8 | `C` sonrasına uzanan `SubmitExperiment` ya da tuning isteği | Reddedilir ve audit'e yazılır | M3 |
| H9 | Tanısal: aynı aile için ikinci değerlendirme; kesim dönemi kotası (10) doluyken istek | Reddedilir ve audit'e yazılır | M3 |
| H10 | Evaluator yanıt şeması | Yalnızca izin verilen alanlar, yuvarlanmış; ham veri yok | M3 |
| H11 | Audit | Manifesto, koşu ve açıklama olaylarının her biri hash'leriyle | M3 |
| H15 | `engineSha` ve image digest uyuşmazlığı | Reddedilir | M3 |
| H16 | Kohort sınırları: 3. açık kohort, kohortta 4. strateji, ailenin 3. ileriye dönük denemesi | Reddedilir | M3 |
| H17 | Aile kimliği: parametre, zaman dilimi, parite, model sürümü ya da engine değişikliği aynı aile; kural grafiği değişikliği yeni aile; benzerlik kontrolü bir kopyayı yakalıyor | Doğru | M3 |
| H18 | İstemcinin beyan ettiği `familyId` | Yok sayılır; sistem kendisi hesaplar | M3 |
| V1 | `Validated` durumunu `results` dışında bir yoldan set etme (Registry, Ops API, Research, Trading rolleri) | Reddedilir | M3 |
| V2 | Paper ve Testnet PnL'i ne olursa olsun doğrulama durumu | Değişmez | M3 |
| V3 | Gücü yetersiz kohort, eksik protokol, yetersiz işlem | En iyi ihtimalle `Inconclusive` | M3 |
| V4 | Enjekte edilmiş saatle `now < A_c + L_c + tampon` iken değerlendirme ya da P import'u (uygulama katmanı **ve** DB trigger'ı ayrı ayrı) | Reddedilir | M3 |
| V5 | Dondurulmuş manifestoda değişiklik; kohort silme ya da geri çekme | Reddedilir | M3 |
| V6 | `Invalid`: kanıtsız istek reddedilir; yeniden değerlendirme yalnızca aynı manifesto, pencere ve digest ile; sınır aşımı reddedilir; tüm koşular audit'te | Doğru | M3 |
| V7 | Aynı girdilerle iki koşu | Aynı sonuç hash'i | M3 |
| V8 | `eval_diagnostic` ile `eval_prospective` birbirinin şemasını; Research ve Trading rolleri holdout şemalarını okuma girişimi | Yetki hatası | M3 |
| V9 | Operasyonel devre dışı bırakma ve kill switch sonrasında kohort sonucu | Hesaplanır ve değişmeden gösterilir | M3 |
| V10 | `PaperTrading → TestnetActive` kullanıcı oturumu olmadan | Reddedilir | M6 |

### 7.2 H-ENV (gerçek ortam, M5)
| # | Test | Beklenen |
|---|---|---|
| H5 | Geliştirme makinesinden holdout DB ve evaluator portlarına bağlantı | Ağ düzeyinde başarısız |
| H6 | Research.Host'tan holdout DB'sine bağlantı | Ağ ya da kimlik doğrulama hatası |
| H6b | Research rolüyle `market.live_candles`, `trading.*` ve açığa çıkmamış kohort view'larını okuma | Yetki hatası ya da 0 satır |
| H13 | Geliştirme makinesinden `ssh -o BatchMode=yes <vps> true` | Başarısız |
| H14 | Trading.Host'tan evaluator'a istek | Ağ düzeyinde başarısız |
| H19 | Gerçek evaluator'da H9, H10, H15, H16, V4 ve V5'in tekrarı | Aynı sonuçlar |
| H20 | Evaluator image digest'i ile `EvaluatorRelease` kaydı | Eşleşir |
| H21 | `eval_*` rolleriyle ops DB'sine bağlantı | Başarısız |
| H22 | Gerçek DB trigger'ı ile erken P import'u | Reddedilir |
| H23 | `ExperimentalPairsMaxExposure` değerini kullanıcı olmayan bir rolle değiştirme | Reddedilir |
| H24 | Ops API: açık kohortun PnL'i görünüyor; manifesto ya da sonuç değiştirme uç noktası yok | Doğru |

H-ENV testlerinin tamamı geçmeden `HoldoutEnvironmentVerified` set edilemez. Bu bayrak yalnızca `Validated` yazımını kapılar. Sahte ortamda geçen testler gerçek izolasyonun kanıtı sayılmaz.

### 7.3 Risk motoru (deneysel parite tavanı, D22)
Risk tablosuna eklenecek satırlar:
- tavanın tam sınırı;
- tavanı aşacak alımın kırpılması ya da reddedilmesi;
- fiyat hareketiyle tavan aşıldığında zorunlu satış yapılmaması ve yeni deneysel girişlerin durması;
- çıkışların her zaman serbest olması;
- mevcut limitlerin (varlık başına %20, toplam %80, 5 pozisyon) değişmemesi.

**Mimari test:** Risk ve Execution modülleri holdout ve kohort modüllerine referans vermez.
