# AllocationProposal gölge pilot protokolü (v1)

- Durum: **Ön kayıt — kullanıcı tarafından onaylandı (2026-10-30, U; `m8-protocol.md` v1.1).** Ön kayıt kanıtı, bu durum satırının güncellendiği commit'tir; kayıt (`alloc-register`) o commit'ten sonra yapılır. Belge commit'lenir ve değişmeden `alloc-register` ile kaydedilir. Kayıtlı hash'i olmayan ya da değiştirilmiş bir belgeyle gölge mod açılmaz (T8, `m8-protocol.md` §4.2).
- Tasarım: [`m8-protocol.md`](m8-protocol.md) §5–§9. Karar kuralı §6.2'de, süre kuralı §6.3'te sabittir; bu belge onları değiştirmez.
- **Pilot nitelik vermez.** Yalnızca günlük fark varyansını (σ̂_d) ve operasyonel oranları ölçer; σ̂_d raporun yalnızca kullanıcıya açık bölümündedir. Yeterlilik penceresinin süresi `N` pilot raporundan §6.3 kuralıyla hesaplanır ve ayrı bir yeterlilik protokolüne yazılır (C3).

## 1. Görev ve kol
- **Görev:** AllocationProposal; gölge mod, router yeterlilik modunda (`JobKind.Qualification`, iş `alloc-<hash12>`); tool yok.
- **Model:** `gemini-3.5-flash-lite` (U-M8-5). Prompt `allocation-prompt v1`, şema `allocation-proposal v1`, bant `allocator-band v1`.
- **Kapsam:** tüm `PaperTrading`/`TestnetActive` stratejilerinin paper run'ları (U-M8-2); Testnet ayna run'ları hariç. Yalnızca canlı mum ve en az bir giriş adayı varsa çağrı yapılır (§5.4).

## 2. Pencere ve bütçe
- **Süre:** 14 gün (A-7). Pencere, bu protokolle açılan `Disabled → Shadow` geçişinden sonraki ilk gölge adımıyla başlar; adımlar bu belgenin hash'iyle etiketlenir.
- **Bütçe:** 1,00 USD iş bütçesi (U-M8-5). Aşılacaksa çağrı yapılmaz (`budget`), gölge T11 ile sürer. Aylık 10 USD tavanı ve LLM acil durdurma aynen geçerlidir.
- **Timeout:** 30 sn (A-6).

## 3. Komutlar (kullanıcının terminali)
1. Kayıt (ağsız): `powershell -ExecutionPolicy Bypass -File scripts/m4-ai.ps1 alloc-register --protocol docs/m8-alloc-pilot-protocol.md`
2. Gölge modu açma: `powershell -ExecutionPolicy Bypass -File scripts/m8-allocator-mode.ps1 -Mode Shadow -ProtocolHash <hash> -Reason "..."`
3. 14 gün sonra rapor: `powershell -ExecutionPolicy Bypass -File scripts/m8-allocator.ps1 alloc-report --protocol docs/m8-alloc-pilot-protocol.md`

```json allocation-protocol
{
  "task": "AllocationProposal",
  "provider": "gemini",
  "model": "gemini-3.5-flash-lite",
  "promptVersion": "allocation-prompt v1",
  "promptHash": "a35e1db64bc413ba163770b396a54fd39b1fea66b4bdb169b12e2163f8d1614f",
  "schemaHash": "3dba851b0cfb0f4f1a18f5ad7d8a99fb7e4e11c8ee8322922660e2aa44856280",
  "band": "allocator-band v1",
  "design": "NonInferiority",
  "role": "pilot",
  "days": 14,
  "budgetUsd": 1.00,
  "alpha": 0.05,
  "marginAnnual": 0.03,
  "seed": 83000
}
```
