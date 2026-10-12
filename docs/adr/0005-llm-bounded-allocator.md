# ADR-0005: LLM'in sınırlı allocator rolü

- Durum: Kabul edildi · Tarih: 2026-10-12

## Bağlam
Her döngüde serbestçe alım/satım kararı veren bir LLM'in performansı geçmiş veri üzerinde test edilemez. Bunun nedenleri: eğitim verisinden kaynaklanan look-ahead, determinizm eksikliği ve maliyet.

## Karar
Sinyaller deterministik ve doğrulanmış stratejilerden gelir. LLM yalnızca:
- bantlar içinde sermaye dağıtabilir,
- yeni girişleri veto edebilir ya da risk azaltma önerebilir,
- karar gerekçesi yazabilir.

LLM çıkışları engelleyemez. LLM çalışmadığında deterministik fallback devreye girer. Allocator önce gölge modda çalışır ve aktif moda geçiş kullanıcı onayıyla olur.

**2026-10-13 eki:** "Deterministik fallback", backtest ve paper'da da kullanılan **varsayılan allocation politikasıdır** (ADR-0011). LLM bu politikanın ağırlıklarını yalnızca bant içinde ayarlayabilir. AllocationProposal görev sınıfı nitelik kazanmadan (ADR-0010) LLM önerisi uygulanmaz.

## Sonuçlar
- Sistemin güvenliği LLM'e bağlı değildir. LLM'in katma değeri gölge modda ölçülür.
