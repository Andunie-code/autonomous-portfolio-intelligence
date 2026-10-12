# ADR-0011: Varsayılan deterministik allocation politikası ve korelasyon yaklaşımı

- Durum: Kabul edildi (teknik lider kararı T11–T12) · Tarih: 2026-10-13
- Uygulama durumu: **Planlandı, uygulanmadı**

## Problem
Önceki belgelerde iki onaysız varsayım vardı:
1. LLM fallback'i olarak "eşit risk ağırlığı ve tavanların yarısı" kuralı.
2. Korelasyon kümesi başına %40 maruziyet tavanı.

İkisinin de ampirik bir dayanağı yoktu. Ayrıca fallback politikası backtest'te hiç kullanılmadığı için **test edilmemiş** bir politika olurdu.

## Alternatifler
| Fallback seçeneği | Değerlendirme |
|---|---|
| Eşit ağırlık, tavanların yarısı | Keyfi bir yarılama; backtest'te doğrulanmamış |
| Eşit ağırlık, onaylı tavanlar | Basit, ama volatilite farklarını yok sayar (kriptoda volatilite paritelere göre çok değişir) |
| **Ters volatilite, onaylı tavanlar** | Standart, basit, deterministik. Risk katkısını dengeler |
| Tam risk paritesi ya da ortalama-varyans | Kovaryans tahmini gerektirir, kararsız; erken optimizasyon |

## Karar
- **Varsayılan allocation politikası (versiyonlu):**
  - aktif sinyali olan varlıklara, gerçekleşmiş volatilitenin tersiyle orantılı ağırlık verilir;
  - ağırlıklar onaylı tavanlarla kırpılır (varlık başına %20, toplam %80, en fazla 5 pozisyon);
  - volatilite verisi eksik olan varlık sıfır ağırlık alır.
- Volatilite penceresi politikanın sürümlenmiş bir parametresidir; M2'de belirlenir ve backtest ile seçilmez (overfitting'i önlemek için sabit, belgelenmiş bir değer).
- **Aynı politika** backtest'te (M2), paper trading'de (M5) ve LLM fallback'inde kullanılır. LLM Allocator (M8) yalnızca bu ağırlıkları bant içinde ayarlayabilir.
- **Korelasyon:** Her döngüde korelasyon kümeleri ve etkin bağımsız pozisyon sayısı hesaplanır, raporlanır ve uyarı üretilir. **Bağlayıcı bir küme tavanı konmaz.** Böyle bir tavan onaylı risk limitlerini değiştireceği için **P1** olarak kullanıcı kararına bırakılır. M5'te tarihsel etki analizi (tavanın getiri, drawdown ve maruziyet üzerindeki etkisi) sunulur.

## Sonuçlar
- Fallback, backtest'te doğrulanmış tek bir politikadır; "test edilmemiş acil durum davranışı" kalmaz.
- Onaylı risk limitleri değişmez. Korelasyon riski görünür olur ama bağlayıcı değildir; kriptoda korelasyonun yüksek olması nedeniyle bu, P1 kararı verilene kadar kabul edilmiş bir risktir.
