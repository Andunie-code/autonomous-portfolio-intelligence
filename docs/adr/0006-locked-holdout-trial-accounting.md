# ADR-0006: Araştırma kesim tarihi, tanısal holdout ve ileriye dönük birincil holdout

- Durum: Kabul edildi · Tarih: 2026-10-12 · Revizyon: 2026-10-13 (rev. 4, kullanıcı kararları U1–U7)
- Uygulama durumu: **Planlandı, uygulanmadı**
- İlgili: ADR-0009 (güven sınırı), ADR-0012 (yaşam döngüsü ve parite kategorileri), `architecture.md` §6

## Bağlam
Önceki tasarımda birincil test, tarihsel ve dönem bazlı bir holdout'tu. Bu tasarımın iki sorunu vardı:
- Tarihsel bir dönemin sonuçları insanlar ve LLM'ler tarafından biliniyor olabilir (bilgi kontaminasyonu).
- "İleriye dönük" bir pencere, ancak stratejinin dondurulmasından **sonra** oluşan veriden oluşuyorsa gerçekten kördür.

Kullanıcı, 2026-01-01'de katı bir araştırma kesim tarihi ve iki ayrı holdout türü istedi.

## Karar
1. **Araştırma kesim tarihi** `C = 2026-01-01T00:00:00Z`. Tüm zamanlar UTC'dir, aralıklar yarı açıktır. Bir mum bir döneme ancak `S ≤ o` ve `o + Δ ≤ E` ise aittir. `C` yalnızca kullanıcı kararıyla ilerletilir; kapanmamış bir ileriye dönük pencereye giremez.
2. **Tanısal holdout** `D_c = [C, A_c)`:
   - Kör değildir.
   - **Eleyebilir, terfi ettiremez.**
   - Aile başına 1 değerlendirme, kesim dönemi başına toplam 10 (U3).
   - Research yalnızca geçti/elendi bilgisini görür.
3. **İleriye dönük birincil holdout** `P_c = [A_c, A_c + L_c)`:
   - Kohort bazlıdır. `A_c` = kohortun dondurulduğu `F_c` anı + 24 saatten sonraki ilk UTC gece yarısı.
   - **`Validated` durumunun tek kaynağıdır.**
   - Süre ön kayıtla seçilir: `L_c = max(L_req, 6 ay)`. `L_req > 12 ay` ise kohort gücü yetersizdir ve en iyi ihtimalle `Inconclusive` olabilir.
   - 6 ve 12 ay varsayılandır, yeterlilik garantisi değildir (U2).
4. **Protokolün tamamlanması:**
   - α, hedef güç, ekonomik δ ve drawdown kuralı **bloke edici TBD**'lerdir (M3'te Protokol v1).
   - Eksik protokolle dondurulan kohort `Validated` olamaz; eksik parametre sonradan eklenemez.
   - İstatistiksel anlamlılık tek başına yeterli değildir: δ ve drawdown kuralı da sağlanmalıdır.
5. **Sonuçlar** (`Validated`, `Failed`, `Inconclusive`, `Invalid`): Tam kurallar `architecture.md` §6.5'te. `Invalid` yalnızca kanıtlı teknik nedenlerle verilir. Yeniden değerlendirme yalnızca aynı manifesto, pencere, kaynak ve digest ile yapılır. Her koşu raporlanır.
6. **Sınırlar (U4):**
   - en fazla 2 açık kohort, kohort başına 3 strateji;
   - aile başına ömür boyu en fazla 2 ileriye dönük deneme (ikinci denemede α/2);
   - aile kimliği sistem tarafından hesaplanır (normalize edilmiş DSL hash'i ve benzerlik kontrolü).

   Bunların hiçbiri tek başına atlatılamazlık garantisi değildir. Global sınırlar ve "n denemeden k Validated" raporlaması asıl güvencedir.
7. **Değiştirilemezlik:** Manifestolar, koşular, sonuçlar ve audit geçmişi append-only'dir; kohortlar silinemez ve geri çekilemez. Operasyonel güvenlik eylemleri (devre dışı bırakma, kill switch, pozisyon kapatma) her zaman serbesttir ve değerlendirmeyi etkilemez (U7).
8. **Deneme muhasebesi:** Tüm denemeler, menşeinden bağımsız olarak (LLM, grid, insan, `dev-assisted`) `TrialFamily` sayacına girer ve Deflated Sharpe'ta kullanılır.

## Gerekçe
- Gerçek körlük, ancak verinin dondurmadan sonra oluşmasıyla sağlanır. Kohort bazlı pencere bunu her kohort için ayrı ayrı garanti eder ve açığa çıkmış bir pencerenin tekrar kullanılmasını imkânsız kılar.
- Tanısal holdout'un asimetrik kullanımı (yalnızca eleme), ona göre ince ayar yapmanın getirisini ortadan kaldırır.
- Gücü yetersiz bir testi "geçti" saymamak, anlamsız bir sonucu başarı olarak raporlamayı önler.

## Sonuçlar ve kabul edilmiş riskler
- İlk `Validated` sonucu en erken `A_0 + 6 ay` sonra gelir. Testnet buna bağlı değildir (ADR-0012).
- Düşük frekanslı stratejiler büyük olasılıkla `Inconclusive` sonuç alır. Bu dürüst bir sonuçtur.
- Tanısal holdout bilgi kontaminasyonuna açıktır. Claude'un eğitim kesim tarihi Haziran 2026; Gemini'ninki doğrulanmadı.
- Root (insan) sınırları aşabilir; saat manipülasyonu kabul edilmiş bir risktir.
- Önceki rev. 3'teki dönem bazlı birincil holdout ve önceki "K1/K2 / ambargo" önerisi bu ADR ile geçersizdir.
