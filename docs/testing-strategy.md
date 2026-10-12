# Test Stratejisi

Son güncelleme: 2026-10-13 (rev. 3) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

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

## 7. Holdout izolasyon testleri (D17, ADR-0006, ADR-0009)

### 7.1 H-DEV (her koşu)
| # | Test | Beklenen |
|---|---|---|
| H1 | Ortam kontrol script'i: dev DB'sinde ilan edilmiş pencerelerdeki mum sayısı | 0 |
| H2 | Importçu ve REST boşluk doldurucuyla pencereyle kesişen istek | Reddedilir ve loglanır |
| H2b | Dosya listesi üretici: pencereyle kesişen aylık zip listede yer almaz; sınır ayı için günlük dosyalar listelenir | Doğru liste |
| H3 | dev DB'sinde pencereye doğrudan satır ekleme | `CHECK` reddeder |
| H4 | Repo ve geliştirme secret/config taraması: holdout DB parolası, evaluator token'ı, VPS anahtarı | Bulunmaz |
| H7 | `GetDatasetCatalog` | Pencereleri içermez |
| H8 | Pencereyle kesişen `SubmitExperiment` ya da tuning isteği | Reddedilir ve audit'e yazılır |
| H9 | (Evaluator, sentetik veriyle) Aynı aile için ikinci değerlendirme | Reddedilir ve audit'e yazılır |
| H10 | (Evaluator) Yanıt şeması | Yalnızca izin verilen alanlar; değerler yuvarlanmış |
| H11 | (Evaluator) Audit | İstek, hash, sonuç, sürüm ve zaman mevcut |
| H15 | (Evaluator) `engineSha` uyuşmazlığı | Reddedilir |
| H16 | (Evaluator) Dönem kotası K doluyken istek | Reddedilir |
| H17 | (Evaluator) Sonuçta `evaluatorVersion` ve `epochId` | Mevcut |
| H18 | (Evaluator) İstemcinin gönderdiği `familyId` yanlışsa | Evaluator kendi hesapladığını kullanır; uyuşmazlık reddedilir |

### 7.2 H-ENV (gerçek ortam, M5)
| # | Test | Beklenen |
|---|---|---|
| H5 | Geliştirme makinesinden VPS'in holdout DB ve evaluator portlarına bağlantı | Ağ düzeyinde başarısız (port yayınlanmamış) |
| H6 | Research.Host container'ından holdout DB'sine bağlantı | Ağ ya da kimlik doğrulama hatası |
| H6b | ops DB'sinde Research rolüyle `market.candles` okuma; `research.candles`'da pencere verisi | Yetki hatası; 0 satır |
| H13 | Geliştirme makinesinden `ssh -o BatchMode=yes <vps> true` | Başarısız (etkileşimsiz SSH yok) |
| H14 | Trading.Host container'ından evaluator'a istek | Ağ düzeyinde başarısız (yalnızca Research.Host ağında) |
| H19 | Gerçek evaluator'da H9, H10, H15, H16 tekrarı | Aynı sonuçlar |
| H20 | Evaluator image digest'i ile `EvaluatorRelease` kaydı | Eşleşir |

H-ENV testlerinin tamamı geçmeden `HoldoutEnvironmentVerified` set edilemez. Sahte ortamda geçmeleri gerçek izolasyonun kanıtı sayılmaz.
