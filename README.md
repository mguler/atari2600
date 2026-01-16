# Atari2600Emu - Sprite Drift Fix (TIA-side)

Bu paket, CPU'yu hiç ellemeyip sadece TIA tarafında sprite kaymasını azaltan iki kritik düzeltme yapar:

1) **Yatay görünür pencereyi 1:1 map etme**
   - Eski sürüm: `x = (cc * 160) / 228` (scale) -> RESP0/HMOVE gibi cycle hassas şeyleri bozar.
   - Yeni sürüm: görünür pencereyi `cc=68..227` kabul edip `x = cc - 68` kullanır.

2) **NUSIZ0 + HMP0/HMOVE**
   - Player0 genişlik/kopyaları ve fine motion (yaklaşık) eklendi.

Not: Bu hala cycle-accurate bir TIA değildir, ama "jitter/kayma"yı ciddi azaltır ve birçok ROM'u daha stabil gösterir.


## VBlank edge fix
`VStart` 233 gibi büyük değerler görüyorsan, bu ROM'un overscan sırasında VBLANK'i tekrar kapatmasından olur.
Bu build sadece **frame başındaki ilk VBLANK OFF** (genelde ~37) geçişini yakalar; geç gelenleri (overscan) yok sayar.


## Player1 + Controls Fix
- TIA: GRP1/RESP1/COLUP1/NUSIZ1/REFP1/HMP1 + HMOVE uygulaması eklendi.
- RIOT: SWCHA yön bitleri düzeltildi (Right=D7, Left=D6). Aktif-low.


## Playfield bit order + Score/Priority (CTRLPF)
- PF0 bit sırası düzeltildi (PF0 ters).
- CTRLPF Score mode (bit1): PF sol yarı COLUP0, sağ yarı COLUP1.
- CTRLPF Priority (bit2): PF ON iken player overwrite engellenir.
Bu özellikle River Raid gibi oyunlarda nehir şekli ve score/ACTIVISION yazısını düzeltir.
