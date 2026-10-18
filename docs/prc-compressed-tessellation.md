> Moved from the development workspace. Names of probes and ledgers in it (`plans/phase4-logs`, `ZzDecodeProbe`) are not part of this repository.

# Compressed tessellation: the rules as settled (2026-10-11, the made axes corrected 2026-10-12)

What the throwaway prototype (`Caly.Rendering.Scene3D.Tests/ZzDecodeProbe.cs`, never committed) does with its
default options, and where each rule comes from. The real decoder is written from this, test first.
Sources: D = the 2009 draft (7.8.9, annex 12.3, 11.1); C = the corpus; I = PDF Association issues
(#773, #540, #436, #750, #772, #774, #775 and, by the owner's decision of 2026-10-11, the
"Pseudo-code Additions" of #727).

State on the first corpus with these rules: calculated normals 1,049/1,049 consumed exactly and on their
origin; stored normals 2,731/2,731 consumed exactly and on their origin; 27 of 27 payloads whole. Second
corpus: 34,464/34,464 consumed exactly.

## One pass

Triangles, points and each triangle's side are decoded together, triangle by triangle (C, I): the side
decides where the neighbours hang, and with stored normals it is only known after the triangle's normal
data is read with its points in place.

## Triangles

- T = number of triangle faces. Point flags R = T + 2C (C seeds). Neighbour flags: the first T values
  of the array (the array has 3T or T values; mask 0x3): 0x1 right neighbour, 0x2 left (D, C, I).
- A stack of gate edges, last in first out. Each entry: the two points of the edge and the third point
  of the triangle that pushed it (its "far" point).
- Stack empty and triangles left -> a seed: three point flags. A point is new (next stored triple) or a
  reference (next value of the reference array: an absolute point index).
  V0 = origin + I0*tol (origin as single precision values), V1 = V0 + I1*tol, V2 = (V0+V1)*0.5 + I2*tol,
  each only if new. (D, C, I)
- Otherwise pop entries until one whose edge is not yet used by two triangles; one point flag; the
  apex is a reference or a new point in the gate edge's frame (below).
- Listing of a triangle: (first, second, third) with first/second the entering edge's points in
  increasing point index (seeds too: their first two points are ordered the same way), third the apex.
- Side ("reversed"): calculated normals: bit t of normal_is_reversed. Stored normals: see Normals.
- Left edge = (first, third) with far point second; right edge = (second, third) with far point first;
  swapped when reversed. A flagged neighbour is pushed unless its edge already carries two triangles;
  right is pushed first, then left (so left is treated next). (D, C, I)
- At the end: every flag, reference, point (and normal bit and angle) consumed, every stacked edge used.

## A new point across a gate edge (Va lower index, Vb higher, Vfar)

    O = (Va + Vb) * 0.5
    X = Va - Vb, unitized                       (annex Unitize)
    Z = X ^ (Vfar - O), unitized; Y = Z ^ X, unitized
    if Z was null (length under FLT_EPSILON):   (Y', Z) = MakeOrthoRep(X) as the annex writes it
                                                (its own unit copy of X; Y axis first, then X axis)
        Y = Z ^ X, unitized                     (X the edge's own, NOT MakeOrthoRep's copy: Y' is dropped)
        if Y . (Vfar - O) < 0:  Y = -Y, Z = -Z  (zero does not turn them)
    P = ((O + X*(a*tol)) + Y*(b*tol)) + Z*(c*tol)

Arithmetic is part of the format (C): doubles, no fused multiply-add, the operations in this order;
Length = Newton's iteration on the sum of squares ROUNDED TO SINGLE PRECISION (annex; a correctly
rounded root, a reciprocal multiply or another order of the sum each lose most tessellations that have
a flat triangle). Equivalent to #773 / ApexFromEdge of #727.

## Normals (stored)

Per triangle, corners in listing order (first, second, third):

- Planar face (is_face_planar): only the first corner of the face's first triangle is treated (through
  that point's state, below); its normal is the face's; every other corner of the face reads nothing.
- A point met for the first time: bits has_multiple, reversed, x_reversed, y_reversed; two angles.
- A point seen with has_multiple = 0: nothing; its normal.
- A point seen with has_multiple = 1: bit is_reference.
  - 1: an index on GetNumberOfBitsUsedToStoreUnsignedInteger(stored - 1) bits (11.1: 1,1,2,3,3,4,4,4...),
    low bit first; the normal is the one `index` places BACK from the most recently stored.
  - 0: bits reversed, x_reversed, y_reversed; two angles; the normal is appended to the point's list.
- Angles: value * (pi/2) / (2^bits - 1): theta then phi.
- Frame of the triangle (listing points F, S, T; V1 = unit(S-F), V2 = unit(T-F), V3 = unit(T-S);
  t1 = |angle(V1,V2) - pi/2|, t2 = |angle(V3,-V1) - pi/2|, t3 = |angle(-V2,-V3) - pi/2|, annex AngleBetween):
  t1 < t2 and t1 < t3: X = V1, Z = V1^V2   (the draft prints X = V3: a misprint, C and I)
  else t2 < t3:        X = V3, Z = -(V3^V1)
  else:                X = -V2, Z = V2^V3
  Z unitized, or made by MakeOrthoRep(X) when null; THEN Z = -Z if reversed; Y = Z ^ X unitized.
  n = X*(±cos(theta)cos(phi)) + Y*(±sin(theta)cos(phi)) + Z*sin(phi), minus where x/y reversed;
  sine and cosine: four terms, folded at pi/4 (D).
- Side of the triangle (for the neighbours): d = ((T - (F+S)*0.5) ^ (S - F)) . n1, where n1 is the
  normal of the FIRST corner as used in this triangle (the face's for a planar face); nothing normalised;
  "reversed" = d < 0 (zero is not reversed). The bits read with new normals do not decide it. (I, C)
  Note the polarity against calculated normals: there the bit itself is "reversed".

## How the last rule was found (2026-10-12), and what it teaches

46 tessellations failed (17 calculated off their origin, 29 stored not consumed), all with flat triangles.
The smallest, Pump-Station #439 (941 triangles, a U-shaped tube handle), has a copy in the same file, #438,
that decoded. Against the copy:

- every point up to about the 400th was within the noise of two separate quantisations (0.7 tolerances on
  average), then the error grew ring by ring: no single wrong step, both made frames on the right side;
- disturbing every new point by 1e-15 of its coordinates moved the last points by 13 tolerances: the
  decode is CHAOTIC. Each ring of a tube of long thin triangles starts across a short edge (7 tolerances)
  with a long arm (38), and each ring start is built on the one before; a dozen such steps multiply an
  error by about 1e11;
- moving ONE early point by ONE unit in the last place (13,500 trials) put all 500 points within 1.3
  tolerances of the copy in 15 of them: the data and the rules were right, a last bit was not;
- over the 128 calculated tessellations with made frames, noise on ordinary points alone broke 106 of the
  111 that landed, and noise on made-frame points alone 64 of them: so ordinary arithmetic was already
  bit-exact, and the difference had to be rare and tied to made frames;
- variants of the made-frame step, scored by "tessellations that land" (weak) and "the 500 points of #439
  within 1.75 tolerances of the copy" (sharp): Y recomputed as unit(Z ^ X) after the turn: 126 of 128;
  recomputed BEFORE the side test: 128 of 128, #439 500 of 500. Everything else tried was worse (X made
  a unit vector again 96; side test from either end of the edge 81 and 80; zero turns 109/126; the
  annex's own start value for Newton's root: no difference at all, it rests on the same number).

Why it is only a bit: MakeOrthoRep's copy of X is X divided by its length once more, and that length is
exactly 1 unless the squares of X add up to something else than 1 in single precision (about one edge in
six). Then Y' and Y agree to sixteen digits and differ in the last.

Lessons for whoever touches this again: (1) a failure that "starts at a flat triangle" may be decided a
few hundred points earlier; (2) a twin in the same file plus "all points near the twin" is a sharp test,
"lands on its origin" is a weak one (many wrong decodes pass it); (3) test a suspected rounding cause by
adding noise, before reading any more of the walk.

## Not settled

- Nothing in the first corpus. In the second, 295 tessellations sit on neither the middle of their box nor
  the mean of their points: whole files (30 of 30, 17 of 17, 14 of 24...), with or without flat triangles,
  from writers that put the origin elsewhere; four payloads of them drawn are clean models
  (`shots2/sheet-c.png`). No tessellation is left out for being off its origin any more.
- Point colours: Task 9, no sample has them.
