# P1 — Korelasyon kümesi maruziyet tavanı: tarihsel etki analizi (M5)

- Durum: **Sonuç — kullanıcı kararı bekliyor (P1).** Ön kayıt: `m5-protocol.md` §9 (`9dda841`). Koşu bir kez yapıldı (2026-10-20, engine `81d2596`).
- Veri: yalnızca araştırma penceresi `R = [2022-01-01, C)`, 20 çekirdek parite, 4h, `DatasetSnapshot a6c4e89b…`. Kohort 0'ın iki stratejisi, `policy` koşusu, tek sleeve, 10.000 USDT, maliyet modeli v1.
- Rapor dosyası (repo dışında): `m5-p1-analysis-20261020T145531Z.json`, SHA-256 `e673ba8969ae575ef89843b603487aa0ea8d19aaa0e24c3e1c24d4002cd852b1`.
- **Tutarlılık kontrolü:** tavansız koşuların M_R değerleri (ema-trend −%3,54, supertrend −%4,99), Kohort 0 manifestosundaki değerlerle aynı.
- **Deneme muhasebesi (T, §9):** strateji tanımları değişmedi ve sonuç hiçbir kapıya ya da kohorta girmiyor. Bu nedenle analiz `TrialFamily` sayacına eklenmedi. Koşu sayısı: 30.

## Birincil sonuçlar (ρ* = 0,70)
| Strateji | Tavan | M_R | Maks. DD | En kötü 30 gün | İşlem | Kırpılan / reddedilen | Ort. maruziyet | En büyük küme payı (maks.) |
|---|---|---|---|---|---|---|---|---|
| ema-trend | %30 | −%3,60 | %16,18 | −%12,11 | 11 | 5 / 5 | %0,6 | %31 |
| ema-trend | %40 | −%3,70 | %18,49 | −%14,16 | 10 | 4 / 5 | %0,8 | %42 |
| ema-trend | %50 | −%3,69 | %18,48 | −%13,95 | 10 | 1 / 1 | %0,8 | %50 |
| ema-trend | %60 | −%3,44 | %17,64 | −%13,01 | 10 | 1 / 0 | %0,8 | %61 |
| ema-trend | yok | −%3,54 | %17,98 | −%13,27 | 10 | 0 / 0 | %0,9 | %79 |
| supertrend | %30 | −%3,44 | %15,17 | −%14,13 | 16 | 4 / 10 | %0,6 | %51 |
| supertrend | %40 | −%3,63 | %16,21 | −%15,95 | 16 | 2 / 4 | %0,6 | %61 |
| supertrend | %50 | −%3,93 | %17,21 | −%17,16 | 17 | 2 / 3 | %0,7 | %64 |
| supertrend | %60 | −%4,43 | %18,92 | −%18,92 | 19 | 2 / 1 | %0,7 | %64 |
| supertrend | yok | −%4,99 | %20,74 | −%20,74 | 20 | 0 / 0 | %0,7 | %79 |

Medyan ve p95 küme payları her koşuda %0'dır, çünkü portföy günlerin çoğunda pozisyonsuzdur. Ortalama etkin bağımsız pozisyon sayısı 1,15–1,27'dir.

**Duyarlılık (ρ* = 0,60 ve 0,80; yalnızca rapor):**
- Yön birincil kesimle aynıdır: tavan supertrend'in DD'sini düşürür, ema-trend'de etki küçük ve karışıktır.
- Bir istisna var: ρ* = 0,80 ve sıkı tavanlarda (%30–40) ema-trend kötüleşir (DD %19,9–20,9; M_R −%4,1 / −%4,4).

## Ön kayıtlı kuralın sonucu
- **Kural koşulları:**
  - tavan en az bir kez bağlamalı;
  - iki stratejide de M_R kaybı en fazla 1,0 puan olmalı;
  - en az bir stratejide DD en az 0,5 puan iyileşmeli.
- **Sonuç:** %30, %40, %50 ve %60'ın hepsi bu koşulları sağlıyor. Kural uygun olanların en büyüğünü önerir: **bağlayıcı küme tavanı %60, uyarı eşiği %54.**

## Sınırlamalar (karar için önemli)
- **Kanıt çok ince:** `policy` koşusunda %15 drawdown halt'ı erken tetiklendiği için portföy dört yılın büyük kısmında nakittedir (ortalama maruziyet %1'in altında). Tavanın etkisi 10–20 işleme ve birkaç kırpmaya dayanır. %60 tavan birincil koşularda yalnızca 1–3 kez bağladı.
- **İki baseline'ın `R`'deki getirisi negatif.** Ölçülen şey kayıp azaltımıdır; avantajlı bir stratejide etki farklı olabilir.
- **Korelasyonun kaynağı tek:** 20 çekirdek paritenin ortak BTC faktörü kümeleri büyütür. Etkin N ≈ 1,2, pozisyonların büyük ölçüde aynı riski taşıdığını gösterir.
- **Genellenemez:** sonuç ileriye genellenemez.

## Karar seçenekleri (kullanıcı, P1)
1. **Kuralın önerisini kabul etmek:** `ClusterCap = (ρ* 0,70, %60)` bağlayıcı olur, uyarı %54'te. Mevcut limitleri sıkılaştırır, gevşetmez. Tarihsel verilere göre nadiren bağlar.
2. **Bağlayıcı tavan koymamak:** yalnızca izleme ve %54'te uyarı. Gerekçe, kanıtın inceliği; konu daha fazla işlem ve maruziyet içeren bir kohortla yeniden değerlendirilir.

Seçilen değer kullanıcı kararıyla `requirements.md` §7'ye (P1) ve `risk-model.md` §2'ye işlenir. Bağlayıcı olursa yeni bir `RiskConfig` sürümü olarak Ops API üzerinden etkinleştirilir; mevcut kohortun manifestosu değişmez.
