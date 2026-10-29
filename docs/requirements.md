# Gereksinimler

Son güncelleme: 2026-10-29 (rev. 5.9; M8 başladı: D34, Kapı A onaylandı) · Değişiklik geçmişi: [CHANGELOG.md](CHANGELOG.md)

> Durum: M0–M5 ve M5b (Kohort 1, M9a dashboard) tamamlandı (roadmap). M6 tamamlandı (kullanıcı kabulü 2026-10-28; gerçek Testnet terfisi D30 sonrasında, `m6-acceptance-report.md` §5). M7 tamamlandı (kullanıcı kabulü 2026-10-28; FindingExtraction yeterliliği `Failed`, web araştırması kapalı, `m7-acceptance-report.md`). M8 başladı (Kapı A onaylandı 2026-10-29: `m8-protocol.md`, ADR-0023). M8 ve sonrasındaki kontroller ve testler **planlanmıştır, henüz uygulanmamıştır.** rev. 5: VPS yok; tüm sistem yerel tek makinede (ADR-0013).

## Karar durumu etiketleri
| Etiket | Anlamı |
|---|---|
| **U** | Kullanıcı onaylı karar. Yalnızca kullanıcı değiştirebilir |
| **T** | Teknik lider kararı. Kullanıcının 2026-10-13'te verdiği yetkiyle alındı; gerekçesi ilgili ADR'de. Teknik gerekçeyle revize edilebilir |
| **P** | Öneri. Kullanıcının yetki alanına girdiği için onay olmadan uygulanmaz |
| **TBD** | Henüz belirlenemeyen değer. Belirleme yöntemi ve zamanı §8'de |

**Teknik liderin yetkisi dışında kalan alanlar** (bunlar için her zaman kullanıcı onayı gerekir):
- Anthropic runtime API harcaması;
- D4 bütçesinin genişletilmesi;
- risk limitlerinin gevşetilmesi ya da emir yetkisinin genişletilmesi;
- holdout izolasyonunun kaldırılması;
- gerçek para ile işlem;
- altyapı satın alınması.

## 1. Vizyon
Kişisel kullanım için bir kripto spot portföy yönetimi ve strateji araştırma sistemi. Sistem:
- strateji hipotezleri üretir ve bunları tekrar üretilebilir deneylerle test eder;
- kanıta göre kabul veya ret kararı verir;
- doğrulanmış stratejileri, önceden tanımlı sınırlar içinde paper trading'de ve Binance Spot Testnet'te otonom çalıştırır;
- her kararı denetlenebilir biçimde kaydeder.

Kârlılık varsayılmaz. Sistemin amacı, bir stratejinin avantajı olup olmadığını **ölçmektir**.

## 2. Kullanıcı onaylı kararlar (U)
| # | Konu | Karar |
|---|---|---|
| D1 | LLM'in operasyonel rolü | Sınırlı allocator. Sinyaller doğrulanmış deterministik stratejilerden gelir. LLM bantlar içinde dağıtım yapar ve yeni girişleri veto edebilir. **Çıkışları engelleyemez** (ADR-0005) |
| D2 | Frekans | 1h–4h mumlar, saatlik karar döngüsü |
| D3 | Terfi (rev. 2026-10-13, U1) | Deterministik kapılardan geçen ve **dondurulan** aday otomatik olarak paper'a girer. Testnet'te `TestnetActive` olması kullanıcı onayı gerektirir. **Testnet için ileriye dönük doğrulama gerekmez.** `Validated` durumu yalnızca ön kayıtlı ileriye dönük değerlendirmeden gelir; paper ya da Testnet sonuçlarıyla verilemez (ADR-0012) |
| D4 | AI bütçesi (Gemini) | Aylık 10–30 USD **sert tavan**. Token ve web maliyetleri dahil, yalnızca Gemini için. 12.000 TL Gemini kredisi bir finansman kaynağıdır, **harcama onayı değildir** |
| D5 | Dağıtım (rev. 2026-10-14) | Tüm sistem kullanıcının yerel bilgisayarında (Windows + Docker Desktop, 16 GB RAM) çalışır. **Harici VPS satın alınmaz ve kullanılmaz.** Mimari hafif tutulur: tek PostgreSQL container'ı, sıkı `mem_limit`/`cpus` sınırları, ağır bileşenler iş bazlı (ADR-0013) |
| D6 | Borsa | Binance Spot Testnet. Adapter borsadan bağımsız bir arayüzün arkasında |
| D7 | Risk varsayılanları | `risk-model.md` §2'deki onaylı satırlar |
| D8 | Dil | Tamamen .NET |
| D9 | Process ayrımı | Trading.Host ve Research.Host ayrı process'ler. Exchange secret'ları yalnızca Trading.Host'ta |
| D10 | Holdout (rev. 2026-10-13) | **Araştırma kesim tarihi `2026-01-01T00:00:00Z`.** Tanısal holdout `[C, A_c)` yalnızca eler. Kohort bazlı ileriye dönük holdout `[A_c, A_c + L_c)`, `Validated` durumunun tek kaynağıdır. Hiçbir LLM rolü holdout'a erişemez (ADR-0006) |
| D21 | Holdout ayrıntıları (U2–U4, 2026-10-13) | `L_min` = 6 ay ve `L_max` = 12 ay **varsayılandır, yeterlilik garantisi değildir.** Gücü yetersiz kohort ya da yetersiz işlem her zaman `Inconclusive` olur. K_D: aile başına ömür boyu 1, kesim dönemi başına 10. En fazla 2 açık kohort × 3 strateji; aile başına en fazla 2 ileriye dönük deneme |
| D22 | Deneysel pariteler (U5, U6) | Çekirdek: 4 yıl; E1: kesim öncesi ≥ 1 yıl kullanılabilir geçmiş; E2: geri kalanlar (yalnızca transfer); listeleme sonrası 30 gün karantina. E1 ve E2'nin toplam maruziyetine `ExperimentalPairsMaxExposure` = **%10** tavan (yapılandırılabilir; yalnızca kullanıcı tarafından `RiskConfig` üzerinden) (ADR-0012) |
| D23 | Operasyonel güvenlik ve değiştirilemezlik (U7) | Kullanıcı açık kohortların PnL'ini görebilir. Manifestolar, sonuçlar ve audit geçmişi değiştirilemez; devre dışı bırakma, kill switch ve pozisyon kapatma her zaman serbesttir ve değerlendirmeyi etkilemez |
| D24 | StatusRelay (2026-10-14) | Ayrı bir StatusRelay container'ı/process'i çalıştırılmaz. HoldoutEvaluator, kısıtlı `status_relay` DB rolüyle `research.cohort_status`'a doğrudan ekleme yapar. Research.Host bu tabloya yazamaz (ADR-0013) |
| D26 | Protokol v1 bloke edici değerleri (B5, 2026-10-16) | Kohort içi α = **0,05** tek yönlü (aile içinde Bonferroni, kohort içinde Holm); hedef güç **0,80**; δ = min(max(**0,05**; **0,50** × backtest excess); **0,15**); δ biçimi **(a)**: alt sınır > 0 **ve** nokta tahmini ≥ δ; ileriye dönük pencerede maks. drawdown **> 0,25** → `Failed`; birincil metriğin benchmark'ı **nakit** (maliyet sonrası mutlak yıllık getiri). Ayrıntı: `m3-protocol.md` §4 |
| D27 | DSL indikatör havuzu (2026-10-16) | M2 indikatörlerine ek olarak SMA, MACD, ADX, Bollinger, Donchian, ROC, Volume SMA ve BTCUSDT piyasa rejim filtresi (aynı kapanış anındaki BTC mumu, look-ahead yok). ADR-0016 |
| D29 | Korelasyon kümesi tavanı (P1, 2026-10-20) | Operasyonda (paper/Testnet) **bağlayıcı** küme maruziyet tavanı: korelasyon kümesi başına portföy değerinin **%60'ı**. Kümeler, son 30 günlük günlük log getirilerinin Pearson korelasyonu, ortalama bağlantılı hiyerarşik kümeleme ve **ρ* = 0,70** kesimiyle oluşturulur. Uyarı eşiği **%54** (bilgilendirme). Tavanı aşacak alım kırpılır ya da reddedilir; zorunlu satış yapılmaz; çıkışlar serbesttir. `RiskConfig`'tedir; yalnızca kullanıcı Ops API üzerinden değiştirir. Araştırma backtest'lerine ve dondurulmuş kohort manifestolarına uygulanmaz. Dayanak ve sınırlamalar: `m5-p1-analysis.md` (kanıt ince) |
| D30 | Asgari paper süresi (2026-10-20) | `PaperTrading → TestnetActive` için en az **14 gün** ve en az **60 işlenmiş 4h mum** paper; son 7 günde **sıfır kritik hata** (reconciliation tutarsızlığı, invariant ihlali, duplicate dolum, hash uyuşmazlığı, HTTP 418). Geçiş yine kullanıcı onayı gerektirir (D3, V10). **Sayım (U, 2026-10-26):** 14 gün, `PaperTrading` lifecycle olayından itibaren takvim günüdür; 60 mum, stratejinin kohort paper run'ında işlenmiş 4h mumlardır ve catch-up mumları dahildir; kesinti oranı ayrıca gösterilir (`m6-protocol.md` §6.1) |
| D33 | Testnet anahtarı ve B6 (2026-10-26) | Testnet API anahtarında statik IP kısıtı aranmaz (dinamik ev IP'si; Testnet yalnızca sanal bakiye kullanır). Telafi edici kontroller: istekler kodda yalnızca `https://testnet.binance.vision` host'una kilitlidir (`TestnetGuardHandler`), 3xx reddedilir, anahtar yalnızca Trading.Host'un Docker secret'larındadır (`testnet_api_key`, `testnet_api_secret`) ve yalnızca işlem iznine sahiptir. **Yalnızca Testnet için geçerlidir;** gerçek borsa ayrı karar ister. B6 kapandı. ADR-0021 |
| D31 | Kohort 1: operasyonel kohort (2026-10-21) | H08 `n150-b300`, H04 `f12-s26-g12` ve H07 `n50-v2p5`, araştırma kapılarını geçmemiş olmalarına rağmen kullanıcı kararıyla **yalnızca operasyonel** kohort olarak dondurulur: protokol tam, `power.underpowered: true`, gerekçe `exemptions`'ta; `Validated` olamaz. Kayıt normal yoldan (`registered_at` ve `A_1` sunucuda). Kohort kendi paper run'ında çalışır. Kapıyı geçmemiş strateji yalnızca bu tür açık bir kararla dondurulabilir. ADR-0019, `cohort-1-preregistration.md` |
| D32 | Dashboard'un öne çekilmesi (M9a, 2026-10-21) | M9'un bugünkü veriyle karşılanabilen kısmı (izlenebilirlik zinciri, DecisionTrace/degraded/LLM acil durdurma, görev sınıfı bazında maliyet) ve sinyal radarı, Trading.Host'ta loopback'te, salt-okunur olarak sunulur. M9'un tam kabulü yerinde kalır. ADR-0020 |
| D28 | E1 verisi M3'te (2026-10-16) | Kategori mantığı çevrimdışı ve sentetik testlerle; ayrıca E1 adaylarından hacme göre ilk 5 parite (1h, 4h) kullanıcının etkileşimli Kapı-2 koşusuyla import edilir |
| D25 | Pozisyon başına hard stop (2026-10-14) | `RiskConfig`'te bağlayıcı bir "pozisyon başına maksimum kayıp" koruyucu stop'u bulunur: giriş fiyatından **%10** (`MaxPositionLossPct = 0.10`, 2026-10-14). Gerekçe: varlık başına %20 tavanla tek bir tam pozisyon portföyde en fazla %2 kaybettirir ve %3 günlük limiti tek başına tüketemez. Strateji stop'u ile birlikte, girişe en yakın olan geçerlidir (ADR-0014) |
| D11 | Kill switch (rev. 2026-10-14) | Pozisyonları tut, yeni emir açma; açık giriş emirleri iptal edilir. **Koruyucu stop emirleri iptal edilmez** (çıkışlar her zaman çalışır, ADR-0014). "Tümünü kapat" ayrı bir manuel eylem |
| D12 | AI sağlayıcısı | **Geçici.** Runtime'da yetkili tek sağlayıcı Gemini (başlangıç modeli `gemini-3.5-flash-lite`). Uygulama şimdilik Anthropic API kullanmaz |
| D13 | Maliyet ayrımı | **Geçici.** Ayrı bir Anthropic faturası yok. Claude Code aboneliğinin uygulamaya runtime API erişimi sağladığı varsayılmaz |
| D14 | Web araştırması | İleride, kontrollü olarak (M7, ADR-0008). **M7 kararları (2026-10-28):**<br>- yalnızca kesim öncesi arXiv sürümleri;<br>- Semantic Scholar ertelendi;<br>- FindingExtraction modeli `gemini-3.5-flash-lite`; bütçe pilot 0,50 USD, yeterlilik 3,00 USD;<br>- arXiv API'si `robots.txt` yerine API koşullarına, diğer yollar `robots.txt`'ye tabi.<br>Ayrıntı: `m7-protocol.md` U-M7-1…U-M7-6, ADR-0022.<br>**Sonuç (2026-10-28):** FindingExtraction `Failed` (`TaskQualification` 2); kullanıcı kararıyla v2 döngüsü açılmadı, web araştırması kapalı (D20) |
| D15 | İlk dikey dilim | M0–M2: veri doğruluğu, iki baseline, benchmark'lar, maliyet modeli, deterministik backtest testleri. LLM, emir, Testnet ve dashboard yok |
| D16 | Model yeterliliği | Erişilebilir ya da fonlanmış olmak uygunluk anlamına gelmez. Karar etkileyen görev sınıfları, ön kayıtlı yeterlilik testini geçmek zorundadır |
| D17 | Holdout izolasyonu (rev. 2026-10-14) | Holdout verisi geliştirme makinesiyle **aynı fiziksel makinede** ve aynı PostgreSQL instance'ında, ayrı bir `holdout` veritabanında bulunur. Bu **kabul edilmiş artık risktir.** İzolasyon DB rolleri ve `CONNECT` yetkileri, `pg_hba` kuralları, Docker ağları, uygulama katmanı ve T17 ile sağlanır. Geliştirme kimlik bilgileriyle okunamaz; araştırma ve tuning iş akışları final değerlendirmeden önce erişemez (ADR-0013) |
| D18 | Sağlayıcıdan bağımsızlık | `IAiProvider`, `ModelRouter`, görev politikaları, çıktı doğrulaması, `DecisionTrace`, `EvidenceValidator` ve değerlendirme altyapısı |
| D19 | Gelecekte Anthropic runtime kullanımı | Mimari seçenek olarak açık. **Ayrı ve açık onay** gerekir; onaylanmış bir bütçe yok |
| D20 | Nitelikli model yoksa | Deterministik davranış, çekimser kalma ya da manuel inceleme. Kalite sessizce düşürülmez |
| D34 | M8 Portfolio Allocator (2026-10-29) | Gölge öneriler **tüm** paper stratejileri için üretilir; aktif modda ağırlık yalnızca `Validated` stratejilere uygulanır. **Bant:** `w' = m × w_T11`, m ∈ [0,5; 1,5] ya da veto; bant dışı çarpan clamp edilir; onaylı tavanlar ardından değişmeden uygulanır. **Model:** `gemini-3.5-flash-lite`; bütçe pilot 1,00 USD, yeterlilik 3,00 USD. **Kapanış:** kabul edilen sapma; yeterlilik penceresi M8 kapanışından sonra arka planda sürer. Ayrıntı: `m8-protocol.md` U-M8-1…U-M8-5, ADR-0023 |

## 3. Teknik lider kararları (T)
| # | Karar | Gerekçe ve ayrıntı |
|---|---|---|
| T1 | **Ortam topolojisi (rev. 5):** tek yerel makine. Docker Compose ile Trading.Host, Research.Host, HoldoutEvaluator ve **tek bir PostgreSQL instance'ı** (`ops` ve `holdout` veritabanları; M0–M2'de yalnızca geliştirme veritabanı). Research.Host ve Evaluator iş bazlı çalışır | ADR-0013 |
| T2 | **Holdout güven sınırı (rev. 5):** holdout verisi yalnızca `holdout` veritabanında ve Docker volume'ünde bulunur. `holdout` DB'ye yalnızca `eval_*` rolleriyle ve yalnızca holdout Docker ağından bağlanılabilir (`pg_hba`). Host'a yayınlanan loopback port üzerinden `holdout` DB'ye ve superuser'a ağ girişi reddedilir. Evaluator'a yalnızca Research.Host'tan, dar bir API ile ulaşılır. Aynı makinedeki geliştiricinin ve Claude Code'un erişebilmesi kabul edilmiş artık risktir | ADR-0013 |
| T3 | Evaluator M3'te sentetik veriyle geliştirilir; gerçek holdout ortamı M5'te aynı yerel makinede kurulur. `HoldoutEnvironmentVerified` **yalnızca `Validated` yazımını** kapılar, Testnet'i etkilemez | ADR-0013, ADR-0012 |
| T4 | **Zaman semantiği:** UTC, yarı açık aralıklar, "tam aralık" mum üyeliği (`S ≤ o` ve `o + Δ ≤ E`). `market.candles` tablosunda **her ortamda** `CHECK (open_time + interval ≤ C)`. Canlı veri ayrı tabloda (`market.live_candles`, yalnızca Trading) | ADR-0006, architecture §6.1–6.2 |
| T5 | **Kohort ve protokol (rev. 4.1; `A_c` kaynağı 2026-10-14'te U kararıyla kesinleşti):**<br>- `registered_at` ve `A_c` (= registered_at + 24 saatten sonraki ilk UTC gece yarısı), yetkili manifesto kaydında **sunucu tarafından** atanır. `A_c`'nin tek yetkili kaynağı `registered_at`'tir; Kohort 0 istisnası architecture §6.3'te.<br>- Manifesto değiştirilemez. GitHub push'u isteğe bağlı ek kanıttır ve kullanıcı onayına tabidir.<br>- Sınırlar, holdout DB'sinde `eval_registry` tarafından uygulanır. Aile kimliği sistem tarafından hesaplanır.<br>- Sonuç tabloları ayrıdır (tanısal: yalnızca `NotRejected` / `Rejected`). `Validated` yalnızca `results.prospective_results`'tan türetilir.<br>- Research'ün açığa çıkma kapısını yalnızca `status_relay` yazar.<br>- Protokolün bloke edici alanları (α, güç, δ, drawdown, δ'nın uygulanma biçimi) M3'te Protokol v1'de belirlenir.<br>- Karar sırası ve `Invalid` politikası architecture §6.5'te | ADR-0006, ADR-0009 |
| T6 | Dosya planlayıcı `≥ C` aralığını kapsayan hiçbir dosyayı listelemez; holdout penceresine değen aylar için günlük dosya kuralı savunma amaçlı olarak korunur | ADR-0006 |
| T7 | Karar etkileyen görev sınıfları (HypothesisGeneration, FindingExtraction, AllocationProposal) yeterlilik testi gerektirir. Danışma niteliğindeki sınıflar (ResultInterpretation, Summary) her çıktıda runtime doğrulamasından geçer ve "LLM tarafından üretildi, danışma amaçlı" etiketi taşır | ADR-0010 |
| T8 | **Ön kayıt:** Yeterlilik protokolü (veri seti hash'i, metrikler, karar kuralı, örneklem) çalıştırılmadan önce hash'lenir. Test aracı (harness) protokol hash'i olmadan çalışmaz. Pilot set ile yeterlilik seti ayrıdır. Güvenlik değişmezlerinde tolerans sıfırdır | ADR-0010 |
| T9 | **Harcama kontrolü:** aylık tavanın başlangıç değeri **10 USD** (D4 aralığının alt sınırı). İş başına ve çağrı başına tavan, eşzamanlılık sınırı, retry politikası, **LLM acil durdurma** ve otomatik tetikler | ADR-0010 |
| T10 | Yeterlilik testleri ve runtime kullanımı Gemini'nin **ücretli katmanında** yapılır. Ücretsiz katman yalnızca sentetik içerikli teknik spike için kullanılır | ADR-0010. Gerekçe: ücretsiz katmanda veri kullanımı ve belgelenmemiş limitler |
| T11 | **Varsayılan allocation politikası:** onaylı tavanlar içinde ters volatilite ağırlıklandırma. Aynı politika backtest'te, paper'da ve LLM fallback'inde kullanılır. Önceki "tavanların yarısı" kuralı kaldırıldı | ADR-0011 |
| T12 | **Korelasyon:** izlenir ve raporlanır. Bağlayıcı küme tavanı, kullanıcı kararıyla D29'dur (P1, 2026-10-20). Önceki onaysız %40 tavanı kaldırıldı | ADR-0011, `m5-p1-analysis.md` |
| T13 | Google Search grounding ve URL context otonom hatta **kullanılmaz.** Kendi getiricimiz provenance için zaten gerekli. Etkileşimli grounding modu bir öneri (P2) | ADR-0008 rev. |
| T14 | **ModelRouter sadeleştirmesi:** sağlayıcılar arası fallback ve görev sınıfı başına aylık bütçe yok. Tek sağlayıcı, nitelikli model listesi, iş ve çağrı bütçesi | ADR-0010 |
| T15 | **Gözlemlenebilirlik:** correlation ID'li yapılandırılmış JSON loglar, health endpoint'leri, DB yetkileriyle append-only audit. Prometheus/Grafana/ELK yok | architecture §11 |
| T16 | **CI:** M0'da tek komutluk yerel doğrulama script'i. Uzak repo seçilince barındırılan CI eklenir | roadmap M0 |
| T17 | **Claude Code korkulukları (rev. 5):** proje talimat dosyası ve proje izin kurallarında holdout ile ilgili yasaklar (holdout DB'sine ve evaluator container'ına bağlanmama; `docker exec`/`docker cp` deny kuralları). Yerel topolojide önemi artar, ama teknik bir güven sınırı değildir | ADR-0013 |
| T18 | Tüm hipotezler (LLM, grid ya da geliştirmede Claude Code destekli) `TrialFamily` sayacına girer. Yapı hash'i nedeniyle bu otomatik olur | ADR-0006 |
| T19 | **Deploy (rev. 5):** kullanıcı yerelde **etkileşimli** olarak `docker compose build` / `up` başlatır. Evaluator image digest'i `EvaluatorRelease` olarak kaydedilir. Harici image registry ve SSH kullanılmaz | ADR-0013 |
| T20 | **Çıkış türleri:** strateji kapanış sinyali (`t` kapanışında üretilir, `t+1` açılışında uygulanır) ve mum içi koruyucu stop. Backtest ve paper aynı deterministik OHLC kuralını kullanır; Testnet'te stop borsada emir olarak bekler | ADR-0014 |
| T21 | **Aile benzerliği:** yeni aday yalnızca aile temsilcileriyle (her ailenin ilk kaydedilen üyesi) karşılaştırılır; temsilci için sıkıştırılmış giriş olayları ve indirgenmiş pozisyon vektörü saklanır | ADR-0015 |
| T22 | **Normalize parity:** parity replay sinyal ve karar düzeyinde birebir; fark raporu mutlak emir büyüklüğü yerine normalize değerleri (birim sinyal, yüzde getiri, işlem zamanlaması) karşılaştırır | ADR-0015 |
| T23 | **YAGNI:** D15 (M0–M2) odaktır. M3+ bileşenleri (DSL, kohort, evaluator, LLM) M1–M2 koduna erken eklenmez | architecture §14 |
| T24 | **Model adları konfigürasyondan:** model adları (ör. `gemini-3.5-flash-lite`) kodda sabit yazılmaz; görev politikalarıyla birlikte konfigürasyondan okunur | architecture §14, ADR-0007 |

## 4. Varsayılanlar
- **Piyasa:** spot, USDT quote. Kaldıraç, margin, futures ve açığa satış yok. (U, Faz A)
- **Pariteler:** Çekirdek evren 20 parite; her biri `[2022-01-01, 2026-01-01)` aralığında kullanılabilir geçmişe sahip. Liste, kayıt zamanındaki bilgiyle sıralanır ve hayatta kalma yanlılığı raporuyla birlikte seçilir. Kullanılabilir geçmiş şartı C'ye kadar hayatta kalmayı koşul koyduğu için evren **survivor-conditioned**'dır; ayrıntılı prosedür `m1-data-policy.md`'de. Daha yeni pariteler E1/E2 kategorilerinde (D22). (U)
- **Exchange anahtarı:** yalnızca trade izni, withdrawal yok. (U) — Testnet anahtarında IP kısıtı yerine kod düzeyinde host allowlist'i ve secret izolasyonu uygulanır (D33, 2026-10-26). Gerçek borsa anahtarı kapsam dışıdır.
- **Strateji temsili:** beyaz listeli DSL (JSON). LLM'in ürettiği kod çalıştırılmaz. (U, ADR-0003)
- **Baseline'lar:** EMA 50/100 trend ve Supertrend. **Benchmark'lar:** BTC buy-and-hold, eşit ağırlıklı sepet, nakit. (U)
- **Paper sermayesi:** 10.000 USDT nominal. (U)
- **Altyapı:** modüler monolit, PostgreSQL, background worker'lar. Redis, RabbitMQ, Kafka ve TimescaleDB yok. (U)
- **Dashboard:** M9'da. Öncesinde API ve minimal bir durum sayfası. (U) — rev. 2026-10-21 (D32): salt-okunur M9a dashboard'u ve sinyal radarı öne çekildi (ADR-0020).

## 5. Kapsam dışı
Gerçek para ile işlem, futures/margin, birden fazla borsa, haber ve sentiment, mobil arayüz, model fine-tuning, LLM'in ürettiği kodun çalıştırılması, sistemin kendi güvenlik kurallarını değiştirmesi, Anthropic runtime kullanımı (D19). Web araştırması M7'den önce kapsam dışı.

## 6. Fonksiyonel olmayan gereksinimler
- **Güvenli varsayılan:** belirsizlikte yeni giriş açılmaz. Çıkışlar her zaman çalışır.
- **Tekrar üretilebilirlik:** veri seti hash'i, strateji sürümü, engine git SHA'sı, maliyet ve sizing modeli sürümleri, seed.
- **Parity:** canlı sistem, backtest ve holdout evaluator aynı kernel kodunu kullanır.
- **Denetlenebilirlik:** emirden veri setine kadar zincir izlenebilir. Audit append-only.
- **Gizlilik:** secret'lar prompt'lara, loglara, repoya ve agent tool'larına giremez.
- **Maliyet:** AI harcaması sert tavanlara tabi; acil durdurma mümkün; aylık rapor.

## 7. Öneriler: kullanıcı onayı gerektirir (P)
| # | Öneri | Neden onay gerekiyor | Ne zaman |
|---|---|---|---|
| ~~P1~~ | ~~Korelasyon kümesi başına bağlayıcı maruziyet tavanı~~ — **karara bağlandı:** D29 (%60, ρ* 0,70, uyarı %54; 2026-10-20) | — | — |
| P2 | Etkileşimli "araştırma asistanı" ekranında Google Search grounding: sonuçlar Search Suggestions ile size gösterilir; otonom hafızaya yazılmaz | Ücretli sorgu harcaması ve şartların kabulü | M9 sonrası, isteğe bağlı |
| P3 | Anthropic/Opus için bir değerlendirme kolu (D19) | Anthropic harcaması | Kanıt toplandığında |
| ~~P4~~ | ~~VPS SSH erişiminde donanım güvenlik anahtarı (FIDO2)~~ — **Geçersiz** (rev. 5, VPS yok) | — | — |

## 8. TBD kaydı
| Parametre | Belirleme yöntemi | Ne zaman | Kim |
|---|---|---|---|
| Kapı eşikleri (min. işlem sayısı, Deflated Sharpe olasılığı, walk-forward tutarlılığı) | M3'te sentetik kalibrasyon: random walk üzerinde hedef yanlış pozitif oranını sağlayan eşikler. **Hedef yanlış pozitif oranının kendisi de TBD**; protokolle birlikte kaydedilir | M3 | Teknik lider, protokolle |
| ~~**BLOKE EDİCİ:** kohort içi α, hedef güç, ekonomik δ, δ'nın uygulanma biçimi, maksimum drawdown kuralı~~ — **karara bağlandı:** D26 (B5) | — | — | — |
| ~~Birincil metriğin kesin formülü, bootstrap blok uzunluğu kuralı~~ — **ön kayıtlı:** `m3-protocol.md` §4 (Kapı A onayıyla) | — | — | — |
| ~~Kapı eşiklerinin değerleri~~ — **kalibre edildi** (Protokol v2): `N_min = 100`, `τ_wf = 0,75`, `τ_nb = 0,80`, `τ_dsr = 0,90` (`m3-calibration-results-v2.md`) | — | — | — |
| ~~Maliyet ve slippage modeli değerleri~~ — **ön kayıtlı:** `m2-protocol.md` v1 §8 | — | — | — |
| Docker `mem_limit` ve `cpus` değerleri | M5'te paper başında ölçüldü (2026-10-20): Trading.Host ≈ 40 MiB / 384 MiB, PostgreSQL ≈ 293 MiB / 1 GiB. Başlangıç değerleri korundu (T). İş bazlı Research.Host ve evaluator'ın tepe kullanımı ölçülmedi; 30 günlük Testnet provasında yeniden ölçülür | M10 | Teknik lider |
| Aile benzerlik eşikleri (korelasyon 0,9, Jaccard 0,8) | Mühendislik başlangıç değerleri; veriyle gözden geçirilir | M3 | Teknik lider |
| Evaluator yayın tamponu (başlangıç: 3 gün) | Arşivin yayın gecikmesine göre. M5'te holdout penceresi verisi import edilmediği için ölçülemedi; ilk gerçek import'ta ölçülür | İlk import (en geç 2027-10, Kohort 0) | Teknik lider |
| Yeterlilik eşikleri ve örneklem büyüklükleri (görev sınıfı başına) | Ayrı bir pilot sette varyans ölçülür, ardından güç analizi yapılır. α ve minimum anlamlı etki protokolde gerekçelendirilir | Her yeterlilik protokolü öncesi | Teknik lider |
| Allocator gölge mod süresi (≥ 4 hafta onaylı alt sınır) | 14 günlük pilotun günlük fark varyansıyla ön kayıtlı süre kuralı: `N = max(28, ⌈(2,487 · σ̂_d / m)²⌉)` gün, en fazla 56 (`m8-protocol.md` §6.3; A-4, U 2026-10-29) | M8 Kapı C3 | Teknik lider, protokolle |
| ~~Timeout, retry, eşzamanlılık değerleri~~ — **M4'te ölçüldü** (spike, pilot ve yeterlilik koşusu: gecikme 0,6–2,0 sn, retry ya da timeout olayı yok). Başlangıç değerleri (120 sn, en fazla 2 retry, eşzamanlılık 1) korundu (`m4-spike-results.md`) | — | — | — |
| ~~Gemini fiyatları, rate limit'leri, kredi kapsamı~~ — **doğrulandı** (`sources.md` S-G1…S-G9, 2026-10-18/19; B1 kapandı, 2026-10-19) | — | — | — |
| TL/USD mutabakatı | Defter, liste fiyatlarına göre USD tutar. Google faturasıyla aylık manuel mutabakat yapılır. Araç M4'te hazır (`m4-ai.ps1 reconcile`). 2026-10 ledger toplamı 0,146228 USD; fatura ay kapanınca girilir | Her ay sonu (ilki 2026-10) | Kullanıcı (fatura tutarı), teknik lider |
| ~~PDF metin çıkarma kütüphanesi~~ — **seçildi** (M7 Kapı A, 2026-10-28): PDF için PdfPig 0.1.16 (Apache-2.0), HTML için AngleSharp 1.8.4 (MIT); native binary yok (ADR-0022, `sources.md` S-X12) | — | — | — |
| Barındırılan CI | Ücretsiz seçenekler değerlendirilir; ücretliyse kullanıcı kararı | İhtiyaç olduğunda | Teknik lider |

## 9. Kullanıcı kararı bekleyen engeller
| # | Konu | Neyi engelliyor |
|---|---|---|
| ~~B1~~ | ~~12.000 TL kredinin Gemini API ücretli katmanına uygulanıp uygulanmadığı~~ — **Kapandı (2026-10-19, U):** kullanıcı, anahtarın faturalandırma hesabı ve kredi tanımlı (ücretli katman) bir Google Cloud projesinde olduğunu; bütçe/harcama limitlerini ve ayrı anahtar kuralını konsolda kontrol ettiğini teyit etti. Uygulama tarafındaki tavanlar (ADR-0010) birincil kontrol olarak kalır | — |
| ~~B2~~ | ~~M5'te VPS satın alımı~~ — **Kaldırıldı** (rev. 5, D5: VPS yok) | — |
| B4 | M1 başlatma onayı; Binance'in açık uç noktalarına (anahtarsız) erişim izni; çekirdek parite seçim prosedürünün onayı. **Durum (2026-10-14): kapandı.** Seçim prosedürü, Kapı 1 ve Kapı 2 onaylandı; M1 kullanıcı kabulüyle tamamlandı (`m1-core-universe.md`) | — |
| B5 | Protokol v1'in bloke edici istatistiksel değerleri. **Durum (2026-10-16): kapandı** (D26) | — |
| ~~B6~~ | ~~Exchange (Testnet) anahtarının IP kısıtı~~ — **Kapandı (2026-10-26, U): D33** | — |

**Not:** İlk `Validated` sonucu, kohortun dondurulmasından en erken 6 ay sonra gelebilir. Testnet buna bağlı değildir.
