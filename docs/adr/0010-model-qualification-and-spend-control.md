# ADR-0010: Model yeterliliği, harcama kontrolü ve LLM acil durdurma

- Durum: Kabul edildi (teknik lider kararı T7–T10, T14) · Tarih: 2026-10-13
- Uygulama durumu: **Kapı B'de uygulandı (2026-10-18)**: harcama kontrolü, `AiKillSwitch` ve otomatik tetikler, ledger, mutabakat raporu ve harness; testing §6.3'teki testler sahte sağlayıcıyla geçti (yapı ADR-0017). Yeterlilik koşusu yapılmadı (Kapı C, B1 gerekli; HypothesisGeneration çerçevesi `m4-protocol.md` §8).
- İlgili: ADR-0007, `architecture.md` §3, `testing-strategy.md` §6

## Problem ve kısıtlar
- Erişilebilir ya da ucuz olmak uygunluk anlamına gelmez (D16). Nitelikli model yoksa sistem güvenli şekilde devam etmeli (D20).
- Bütçe: aylık 10–30 USD (D4). Kredi harcama onayı değildir. Fiyatlar, limitler ve kredi kapsamı doğrulanmadı.
- Sonuçlar görüldükten sonra eşik değiştirmek (p-hacking) önlenmeli. Dayanaksız istatistiksel eşik uydurulmamalı.

## Karar

### Yeterlilik
- **Karar etkileyen** görev sınıfları (HypothesisGeneration, FindingExtraction, AllocationProposal) nitelik şartına tabidir.
- **Danışma** sınıfları (ResultInterpretation, Summary) nitelik gerektirmez. Her çıktı runtime'da doğrulanır, etiketlenir ve hiçbir kararı etkilemez. Gerekçe: gereksiz yeterlilik yükünden kaçınmak; bu sınıflar otomatik kararlara girmiyor.
- **Ön kayıt:**
  - Pilot set ile yeterlilik seti ayrıdır.
  - Protokol (set hash'i, birincil metrik, karar kuralı, α, minimum etki, güç analiziyle örneklem, bütçe) koşudan önce hash'lenir.
  - Harness protokol hash'i olmadan çalışmaz.
  - Değişiklik yeni bir protokol ve yeni bir set gerektirir.
- **Değerler:**
  - Güvenlik değişmezlerinde tolerans 0 (istatistiksel değil, tanım gereği).
  - α, etki büyüklüğü ve örneklem: protokol başına, pilot varyansıyla gerekçelendirilir (TBD).
  - Güç yetersizse sonuç "belirsiz" olur ve **nitelik verilmez.**
- Yeterlilik ve runtime ücretli katmanda yapılır. Ücretsiz katman yalnızca sentetik spike içindir (T10).

### Harcama kontrolü (T9)
| Katman | Başlangıç değeri |
|---|---|
| Aylık tavan | **10 USD** (D4'ün alt sınırı). Artırmak D4 aralığında teknik karar; aralığın dışı kullanıcı kararı |
| İş başına tavan | Yeterlilik koşusu: aylık tavanın %30'u. Araştırma döngüsü: %5 |
| Çağrı başına | `EstimateMaxCost`, `maxOutputTokens` ile rezerve edilir |
| Eşzamanlılık | 1 |
| Retry | En fazla 2 (429, 5xx, ağ hatası); üstel bekleme + jitter; `Retry-After`. 4xx'te retry yok. Şema hatasında tek bir onarım isteği |
| Timeout | 120 sn |

Ledger USD cinsinden liste fiyatlarıyla tutulur. Google faturasıyla aylık mutabakat yapılır; fark %10'u aşarsa fiyat tablosu güncellenir. Geliştirme ve runtime için ayrı API anahtarları kullanılır. (2026-10-14: yerel tek makine topolojisinde ikisi de Claude Code ile aynı makinededir; kabul edilmiş artık risk, ADR-0013.)

### LLM acil durdurma
- `AiKillSwitch`: ops DB'sinde kalıcı bir bayrak. Her çağrıdan ve her retry'dan önce kontrol edilir.
- Açıldığında: yeni çağrı yapılmaz, uçuştaki çağrılar iptal edilir, Batch işleri iptal edilir, görevler `onUnavailable`'a düşer.
- **Otomatik tetikler:**
  - aylık tavana ulaşmak;
  - son 60 dakikadaki harcamanın aylık tavanın %20'sini aşması;
  - art arda 3 çağrıda gerçek maliyetin tahmini 2 katını aşması;
  - circuit breaker'ın açılması.
- Bayrak yalnızca manuel kapatılır ve audit'e yazılır.
- **Bant dışı:** Google konsolunda API anahtarının iptali (runbook).
- Trading kill switch'inden bağımsızdır; LLM'in durması trading'i durdurmaz.

## Gerekçe
- Harcama tavanı aralığın alt ucundan başlar, çünkü gerçek token profili henüz bilinmiyor. M4'teki ölçüm sonrasında aralık içinde ayarlanabilir.
- Harcama hızı tetiği, tavan dolmadan bir hata döngüsünü yakalar. %20 ve 2 kat değerleri istatistiksel değil, mühendislik güvenlik payı olarak seçilmiş başlangıç değerleridir.
- Güç yetersizliğinde nitelik vermemek, "anlamsız bir sonucu başarı saymayı" önler.

## Riskler
- Geliştirme makinesindeki API anahtarı kodumuzu atlayarak kullanılabilir. Google tarafındaki limitler ve aylık mutabakat ile yakalanır.
- Bütçe, yeterlilik için gerekli örneklemi karşılamayabilir. Sonuç "belirsiz" olur ve sistem LLM'siz modda devam eder.
