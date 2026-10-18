# Doğrulanmış Harici Bilgiler

Son güncelleme: 2026-10-18

Bu belgedeki bilgiler, aksi belirtilmedikçe, belirtilen tarihte resmi kaynaktan okunmuştur. Fiyatlar, limitler ve şartlar değişebilir. Her bilgi, ilgili milestone başlamadan önce yeniden doğrulanır.

## Binance (doğrulama tarihi: 2026-10-12)
| ID | Bilgi | Kaynak |
|---|---|---|
| S-B1 | Spot Testnet REST adresi `https://testnet.binance.vision/api`; API anahtarı GitHub girişiyle alınır | https://developers.binance.com/docs/binance-spot-api-docs/testnet/rest-api/general-api-information |
| S-B2 | Rate limit'ler IP'ye bağlıdır. `X-MBX-USED-WEIGHT-*` header'ı kullanımı gösterir. Limit aşımında 429, ısrar edilirse 418 (2 dakikadan 3 güne kadar IP ban) | aynı |
| S-B3 | Timeout ve 5xx durumunda emrin akıbeti bilinmez; emir motorda gerçekleşmiş olabilir. Önerilen yol durumu sorgulamaktır | aynı |
| S-B4 | Açık bir emirle aynı `newClientOrderId` ile gönderilen yeni emir reddedilir | aynı |
| S-B5 | Emir filtreleri: PRICE_FILTER, LOT_SIZE, NOTIONAL/MIN_NOTIONAL, MARKET_LOT_SIZE, MAX_NUM_ORDERS, PERCENT_PRICE_BY_SIDE | https://developers.binance.com/docs/binance-spot-api-docs/filters |
| S-B6 | Tarihsel kline verisi: günlük ve aylık zip dosyaları, her biri için `.CHECKSUM` (SHA-256). Aralıklar 1s'den 1mo'ya kadar | https://github.com/binance/binance-public-data |
| S-B7 | Spot verisinde 2025-01-01'den itibaren zaman damgaları **mikrosaniye** cinsinden | aynı |

| S-B8 | Arşiv listelemesi: `https://data.binance.vision/?prefix=...` HTML web sayfası döndürür (2026-10-14 M1 Kapı 2 denemesinde gözlendi). Path-style S3 listeleme uç noktası `https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=...&delimiter=/`; dosyalar `https://data.binance.vision/data/...` üzerinden. **Kaynak: kullanıcı beyanı; S3 uç noktasının yanıtı henüz gözlenmedi** (ilk başarılı listelemede doğrulanır) | Kullanıcı, 2026-10-14 |

**Doğrulanamayanlar (varsayım):** Testnet'in sıfırlanma sıklığı; Testnet emir defterinin ve likiditesinin gerçek piyasayı temsil etmediği (yaygın bilgi, resmi dokümanda bulunamadı).

## Google Gemini (ilk doğrulama: 2026-10-12; M4 öncesi yeniden doğrulama: 2026-10-18)
Yalnızca resmi belge sayfaları okundu; API çağrısı yapılmadı.

| ID | Bilgi | Kaynak |
|---|---|---|
| S-G1 | `gemini-3.5-flash-lite` Stable (sayfa güncellemesi 2026-07-30). Girdi limiti 1.048.576 token, çıktı limiti 65.536 token. Desteklenenler: structured outputs, function calling, Batch API, Flex ve Priority çıkarımı, context caching, thinking, search grounding, URL context, code execution, file search. Bilgi kesim tarihi ve kullanımdan kalkma tarihi sayfada yok | https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash-lite · https://ai.google.dev/gemini-api/docs/models |
| S-G2 | Ücretli katman fiyatı (1M token başına; sayfa güncellemesi 2026-10-16):<br>- Standart: girdi 0,30 $, çıktı **2,50 $ (thinking token'ları dahil)**.<br>- Batch ve Flex: 0,15 $ / 1,25 $.<br>- Priority: 0,54 $ / 4,50 $.<br>- Context caching yalnızca örtük: 0,03 $ + saat başına 1,00 $ depolama.<br>Ücretsiz katmanda girdi ve çıktı ücretsiz; caching ve grounding yok | https://ai.google.dev/gemini-api/docs/pricing |
| S-G3 | Google Search grounding (Gemini 3.5 ve 3.1 Flash-Lite): ücretsiz katmanda **yok**. Ücretli katmanda Gemini 3+ modelleri arasında paylaşılan aylık 5.000 ücretsiz sorgu, sonrası 1.000 sorgu başına 14 $. Gemini 3+ modellerinde faturalama sorgu başına yapılır. URL context aracı ücretsizdir ama getirilen içerik girdi token'ı olarak faturalanır (2026-10-12; M7 öncesinde yeniden doğrulanacak) | pricing · https://ai.google.dev/gemini-api/docs/google-search |
| S-G4 | Katmanlar (sayfa güncellemesi 2026-10-15):<br>- Tier 1: faturalama hesabı bağlanınca; 250 $ katman tavanı ve kayan 10 dakikada 10 $ harcama limiti.<br>- Tier 2: 100 $ ödeme ve ilk ödemeden 3 gün sonra; 2.000 $ / 50 $.<br>- Tier 3: 1.000 $ ödeme ve 30 gün; 200 $.<br>Limitler API anahtarı başına değil **proje başına** uygulanır; harcama limitine ulaşılınca 429 `RESOURCE_EXHAUSTED` döner; uygulanması fatura geçmişine bağlı. Ücretsiz katmanın RPM/TPM/RPD değerleri AI Studio'dan okunmalı | https://ai.google.dev/gemini-api/docs/rate-limits |
| S-G5 | Şartlar (sayfa güncellemesi 2026-04-28, yürürlük 2026-03-23):<br>- Ücretsiz katman: gönderilen içerik ve yanıtlar ürün geliştirmede kullanılır; hesaptan ayrıştırıldıktan sonra insanlar tarafından okunabilir; hassas bilgi gönderilmemelidir.<br>- Ücretli katman: içerik ürün geliştirmede kullanılmaz; DPA kapsamında, yalnızca kötüye kullanım tespiti ve yasal yükümlülükler için sınırlı süre loglanır.<br>- Hizmete finansal tavsiye için güvenilmemelidir.<br>Grounding koşulları: 2026-10-12 kaydı geçerli (M7 öncesinde yeniden doğrulanacak) | https://ai.google.dev/gemini-api/terms |
| S-G6 | Resmi .NET SDK'sı `Google.GenAI` (NuGet). En yeni kararlı sürüm 1.26.1 (2026-10-14); önceki sürümler 1.26.0 (10-13), 1.25.0 (10-07), 1.24.0 (10-01), 1.23.0 (09-30). Hedefler: net8.0, netstandard2.0. net8.0 bağımlılıkları: `Google.Apis.Auth` ≥ 1.69.0, `Microsoft.Extensions.AI.Abstractions` ≥ 10.6.0, `MimeTypes` ≥ 2.5.2, `System.Text.Json` ≥ 10.0.8. API referansında doğrulananlar:<br>- `Client(…, HttpOptions? httpOptions, ClientOptions? clientOptions)`;<br>- `ClientOptions.HttpClientFactory` (`Func<HttpClient>`);<br>- `HttpOptions` alanları: `BaseUrl`, `ApiVersion`, `Headers`, `RetryOptions`, `Timeout` (ms);<br>- `Models.GenerateContentAsync(model, contents, config, CancellationToken)`;<br>- yanıt alanları: `ModelVersion`, `ResponseId`, `PromptFeedback`, `UsageMetadata` (`PromptTokenCount`, `CandidatesTokenCount`, `ThoughtsTokenCount`, `ToolUsePromptTokenCount`, `CachedContentTokenCount`, `TotalTokenCount`);<br>- `client.Batches` ile oluşturma, sorgulama, iptal, listeleme ve silme | https://googleapis.github.io/dotnet-genai/ · https://googleapis.github.io/dotnet-genai/api/index.html · https://www.nuget.org/packages/Google.GenAI |
| S-G7 | Structured output ile tool'ların birlikte kullanımı **önizleme** durumunda ve "yalnızca Gemini 3 serisi" için belirtilmiş; 3.5 ayrıca anılmıyor (sayfa güncellemesi 2026-10-13). Desteklenen JSON Schema alt kümesi:<br>- object: `properties`, `required`, `additionalProperties`;<br>- string: `enum`, `format`;<br>- sayı: `enum`, `minimum`, `maximum`;<br>- array: `items`, `prefixItems`, `minItems`, `maxItems`.<br>Örneklerde `$ref` ve `anyOf` kullanılıyor ama desteklendikleri açıkça yazılmıyor. Sayısal derinlik veya boyut sınırı belgelenmemiş: "çok büyük ya da derin şemalar reddedilebilir" | https://ai.google.dev/gemini-api/docs/structured-output |
| S-G8 | Thinking (sayfa güncellemesi 2026-10-14; 2026-10-19'da okundu): `max_output_tokens` thinking token'larını **içerir**; sınıra düşünme sırasında ulaşılırsa çıktı kesik ya da boş olabilir, thinking token'ları yine faturalanır. `gemini-3.5-flash-lite`'ta thinking varsayılan olarak açık, seviye **minimal** (minimal/low/medium/high). Durumsuz kullanımda (geçmişi istemci yeniden gönderir) modelin döndürdüğü thought blokları "alındığı gibi" geri gönderilmelidir; eksik olursa ne olduğu yazmıyor. Adapter model turunu opak olarak aynen geri gönderir (ADR-0017) | https://ai.google.dev/gemini-api/docs/thinking |
| S-G9 | `gemini-3.8-flash` (2026-10-19'da okundu): ücretli katman fiyatı (1M token başına) girdi **0,75 $**, çıktı **3,75 $** (thinking dahil) 2026-12-31'e kadar; **2027-01-01'den itibaren 1,50 $ / 7,50 $**. Sayfada "Preview" etiketi yok, ama kararlı olduğu da açıkça yazmıyor. Yalnızca spike'ta seçmeli model olarak fiyat tablosuna eklendi; varsayılan model değişmedi (D12). Sorun giderme sayfası: 400 hatası geçersiz API anahtarı, tükenmiş ön ödemeli kredi, sözdizimi hatası, aralık dışı parametre ya da desteklenmeyen özellikten kaynaklanabilir | https://ai.google.dev/gemini-api/docs/pricing · https://ai.google.dev/gemini-api/docs/troubleshooting |

**Doğrulanacaklar (varsayım):**
- Structured output ile function calling'in `gemini-3.5-flash-lite` üzerinde birlikte çalışması (M4 Kapı C spike'ı).
- `ModelVersion` alanının biçimi ve yapılandırılmış model adıyla eşleşmesi (spike).
- `ClientOptions.HttpClientFactory` ile enjekte edilen istemcinin tüm çağrılarda kullanılması ve SDK retry'ının kapatılabilmesi (Kapı B, çevrimdışı testle).
- Ücretsiz katmanın gerçek limitleri (AI Studio'dan).
- ~~Thinking token'larının çıktı fiyatından faturalanıp faturalanmadığı~~ — **doğrulandı** (S-G2, 2026-10-18: çıktı fiyatına dahil).

**Gemini kredisi (kullanıcı beyanı, 2026-10-13):** 12.000 TL Gemini kredisi mevcut. Kredinin Gemini API ücretli katmanına uygulanıp uygulanmadığı, kapsamı ve son kullanma tarihi **doğrulanmadı** (M4 öncesinde). Kredi bir finansman kaynağıdır, harcama onayı değildir (D4).

## Anthropic: yalnızca gelecekteki bir seçenek için referans (doğrulama tarihi: 2026-10-13)
> Anthropic runtime API kullanımı **yetkili değildir** (D12, D19). Bu bilgiler yalnızca ileride yapılacak bir değerlendirme için kaydedilmiştir ve o zaman yeniden doğrulanmalıdır.

| ID | Bilgi | Kaynak |
|---|---|---|
| S-A1 | Claude Opus 5.5, API kimliği `claude-opus-5-5`. 1M girdi ve 128K çıktı token'ı. Adaptive thinking her zaman açık, varsayılan effort `medium`. Eğitim verisi kesim tarihi Haziran 2026. Emeklilik 2027-09-22'den önce değil | https://platform.claude.com/docs/en/models/overview |
| S-A2 | Opus 5.5 fiyatı (1M token başına): girdi 4 $, çıktı 20 $, 5 dakikalık cache yazma 5 $, cache okuma 0,20 $; Batch 2 $ / 10 $. Uzun bağlam için ek ücret yok. `inference_geo: "us"` 1,1× fiyat | https://platform.claude.com/docs/en/about-claude/pricing |
| S-A3 | Türkiye, Claude API'nin desteklendiği ülkeler arasında | https://www.anthropic.com/supported-countries |

**Doğrulanmayanlar:**
- Opus 5.5 API kısıtları: zorunlu tool seçimi reddi, refusal işleme, append-only thinking kuralı. Kaynak, Claude API referans dokümanı; canlı dokümandan doğrulanmadı.
- C# SDK'sının paket adı.
- Yeni bir organizasyonun rate limit katmanları.
- Claude Code aboneliğinin uygulamaya runtime API erişimi sağlayıp sağlamadığı ya da headless kullanımla ilgili şartları. **Sağlamadığı varsayılır** (D13).

## Henüz doğrulanmayanlar (M7 öncesinde)
- arXiv API ve Semantic Scholar API: kullanım koşulları, rate limit'ler, ücretsiz erişim.
- ~~VPS fiyatları~~ — gerekmiyor (2026-10-14, D5 rev.: VPS yok).
- Binance Spot'ta koruyucu stop emir tipleri ve Testnet desteği (M6 öncesinde; ADR-0014).
