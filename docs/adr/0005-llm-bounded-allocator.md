# ADR-0005: LLM'in sınırlı allocator rolü

- Durum: Kabul edildi · Tarih: 2026-10-12
- **M8 notu (2026-10-29):** uygulama ayrıntıları ADR-0023 ve `m8-protocol.md`'de: bant `m × w_T11`, m ∈ [0,5; 1,5] ya da veto (D34); öneri yalnızca giriş adaylarına uygulanır; çıkışlara ve stop'lara müdahale girişimi güvenlik olayıdır. "Risk azaltma önerisi" M8'de veto ve düşük çarpanla sınırlıdır; mevcut pozisyonlar için öneri yoktur.

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
