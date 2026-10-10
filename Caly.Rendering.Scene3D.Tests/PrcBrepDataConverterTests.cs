using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Brep;
using Caly.Rendering.Scene3D.Prc.Generated;
using Caly.Rendering.Scene3D.Prc.Tessellation;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>Uncompressed bodies (<c>PRC_TYPE_TOPO_BrepData</c>) turned into the body model the tessellator draws.</summary>
public sealed class PrcBrepDataConverterTests
{
    /// <summary>
    /// Builds an entity graph as a file stores it: an entity is stored where it is first used and named by its number after that,
    /// the numbers given from 1 in file order, an entity before what it holds (the phase B rule).
    /// </summary>
    private sealed class Graph
    {
        private readonly Dictionary<object, uint> _numbers = new(ReferenceEqualityComparer.Instance);
        private uint _next;

        /// <summary>The entity stored here; its number is taken before <paramref name="make"/> stores what it holds.</summary>
        public PrcPtrTopology Store(uint type, Func<object> make)
        {
            uint number = ++_next;
            object entity = make();
            _numbers.Add(entity, number);
            return new PrcPtrTopology { IsStored = false, Topo = new PrcTopo { Id = entity, IdType = type }, TopoIdentifier = 0 };
        }

        /// <summary>The entity stored here the first time, named by its number after that.</summary>
        public PrcPtrTopology Use(ref object? entity, uint type, Func<object> make)
        {
            if (entity is not null)
            {
                return new PrcPtrTopology { IsStored = true, Topo = null, TopoIdentifier = _numbers[entity] };
            }

            PrcPtrTopology stored = Store(type, make);
            entity = stored.Topo!.Id;
            return stored;
        }
    }

    private static PrcTransformation3D Frame(PrcVector origin, PrcVector x, PrcVector y) => new()
    {
        Behavior = 0x03,
        Translation = new PrcVector3D { X = origin.X, Y = origin.Y, Z = origin.Z },
        NonOrthoMatrix = [],
        Rotation = [Vec(x), Vec(y), Vec(x.Cross(y))],
        NonUniformScale = default,
        Scale = 1,
        Homogeneous = [],
    };

    private static PrcVector3D Vec(PrcVector v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    private static PrcPtrCurve Inline(object curve, uint type) => new() { IsReferenced = false, Curve = new PrcCrv { Id = curve, IdType = type }, CurveIdentifier = 0 };

    private static PrcUVParameterization Identity => new() { SwapUv = false, SufaceDomain = new PrcDomain { MinUv = new PrcVector2D { X = 0, Y = 0 }, MaxUv = new PrcVector2D { X = 2 * Math.PI, Y = 5 } }, UParamCoeffA = 1, UParamCoeffB = 0, VParamCoeffA = 1, VParamCoeffB = 0 };

    private static PrcPtrCurve NoCurve => new() { IsReferenced = false, Curve = null, CurveIdentifier = 0 };

    private static PrcParameterization Parameters(double from, double to) => new() { TrimInterval = new PrcInterval { MinValue = from, MaxValue = to }, CoeffA = 1, CoeffB = 0 };

    private static PrcCrvLine Line() => new() { CurveData = new PrcContentCurve { Is3dFlag = true }, Transform3d = null, Parameterization = Parameters(0, 1) };

    private static PrcTopoBody Body(params PrcPtrTopology[] connex) => new() { Id = new PrcTopoBrepData { Connex = connex }, IdType = PrcType.PRC_TYPE_TOPO_BrepData };

    private static PrcTopoContext Context(params PrcTopoBody[] bodies) => new() { Tolerance = 1e-6, HasScale = false, Scale = 0, Bodies = bodies };

    private static PrcTopoBrepDataCompress Convert(PrcTopoContext context, int body = 0)
    {
        PrcBrepDataConverter.Tables tables = PrcBrepDataConverter.Index(context);
        Assert.True(PrcBrepDataConverter.TryConvert((PrcTopoBrepData)context.Bodies[body].Id!, tables, context.Tolerance, out PrcTopoBrepDataCompress result, out string? error), error);
        return result;
    }

    private static (PrcVector[] Points, int[] Indices, PrcBrepTessellation Result) Tessellate(PrcTopoBrepDataCompress body)
    {
        using var builder = new SceneBuilder(SceneLimits.Default);
        PrcMesh mesh = PrcBrepTessellator.AddMesh(builder, body, builder.AddName("body"), out PrcBrepTessellation result);
        Assert.True(mesh.Mesh >= 0, "a mesh was made");
        using Scene scene = builder.Build();
        ReadOnlySpan<float> p = scene.Positions;
        var points = new PrcVector[p.Length / 3];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new PrcVector(p[3 * i], p[(3 * i) + 1], p[(3 * i) + 2]);
        }

        return (points, [.. scene.Indices.ToArray().Select(i => (int)i)], result);
    }

    private static double Volume((PrcVector[] Points, int[] Indices, PrcBrepTessellation _) m)
    {
        double volume = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            volume += m.Points[m.Indices[t]].Dot(m.Points[m.Indices[t + 1]].Cross(m.Points[m.Indices[t + 2]])) / 6;
        }

        return volume;
    }

    private static double Area((PrcVector[] Points, int[] Indices, PrcBrepTessellation _) m)
    {
        double area = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            PrcVector a = m.Points[m.Indices[t]], b = m.Points[m.Indices[t + 1]], c = m.Points[m.Indices[t + 2]];
            area += Math.Sqrt((b - a).Cross(c - a).LengthSquared()) / 2;
        }

        return area;
    }

    /// <summary>
    /// The unit cube: six plane faces of one loop of four line edges each; every edge after its first use, and every vertex after
    /// its first, is named by its number.
    /// </summary>
    private static PrcTopoContext Cube(Graph g)
    {
        PrcVector[] corners = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        var vertices = new object?[8];
        var edges = new Dictionary<(int, int), object?>();
        PrcPtrTopology Vertex(int i) => g.Use(ref vertices[i], PrcType.PRC_TYPE_TOPO_UniqueVertex, () => new PrcTopoUniqueVertex { Vertex = Vec(corners[i]) });
        PrcPtrTopology Edge(int a, int b)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));
            object? edge = edges.GetValueOrDefault(key);
            PrcPtrTopology pointer = g.Use(ref edge, PrcType.PRC_TYPE_TOPO_Edge, () => new PrcTopoEdge
            {
                WireEdge = new PrcContentWireEdge { PtrCurve = Inline(Line(), PrcType.PRC_TYPE_CRV_Line), IsTrimmed = false, TrimInterval = default },
                StartVertex = Vertex(key.Item1),
                EndVertex = Vertex(key.Item2),
            });
            edges[key] = edge;
            return pointer;
        }

        PrcPtrTopology Face(int[] f) => g.Store(PrcType.PRC_TYPE_TOPO_Face, () =>
        {
            PrcVector o = corners[f[0]], x = corners[f[1]] - o, y = corners[f[3]] - o;
            return new PrcTopoFace
            {
                IsTrimmed = false,
                TrimmedSurface = null,
                SurfaceGeometry = new PrcPtrSurface { IsReferenced = false, Surface = new PrcSurf { Id = new PrcSurfPlane { Transform = Frame(o, x, y), UParameterCoeffA = 1, UParameterCoeffB = 0, VParameterCoeffA = 1, VParameterCoeffB = 0 }, IdType = PrcType.PRC_TYPE_SURF_Plane }, SurfaceIdentifier = 0 },
                IndexOfOuterLoop = 0,
                Loops =
                [
                    g.Store(PrcType.PRC_TYPE_TOPO_Loop, () => new PrcTopoLoop
                    {
                        LoopOrientation = 1,
                        Coedge = [.. Enumerable.Range(0, 4).Select(k => new PrcCoedgeInLoop
                        {
                            NextCoedge = g.Store(PrcType.PRC_TYPE_TOPO_CoEdge, () => new PrcTopoCoEdge { PtrTopology = Edge(f[k], f[(k + 1) % 4]), PtrCurves = NoCurve, CoedgeOrientation = 1, UvOrientation = 1 }),
                            NeighborIndex = 0,
                        })],
                    }),
                ],
            };
        });

        PrcPtrTopology connex = g.Store(PrcType.PRC_TYPE_TOPO_Connex, () => new PrcTopoConnex
        {
            Shells =
            [
                g.Store(PrcType.PRC_TYPE_TOPO_Shell, () => new PrcTopoShell
                {
                    IsClosed = true,
                    Faces = [.. faces.Select(f => new PrcFacesInShell { Face = Face(f), Orientation = 1 })],
                }),
            ],
        });
        return Context(Body(connex));
    }

    [Fact]
    public void ACubeOfPlanesAndLinesWithItsSharedEdgesNamedByNumberIsTheUnitCube()
    {
        PrcTopoBrepDataCompress body = Convert(Cube(new Graph()));

        Assert.Equal(8, body.Vertices.Length);
        Assert.Equal(12, body.Curves.Length);
        Assert.Equal(6, body.Faces.Length);
        var m = Tessellate(body);
        Assert.Equal((6, 0), (m.Result.Faces, m.Result.FacesDropped));
        Assert.Equal(1, Math.Abs(Volume(m)), 6);
        Assert.Equal(6, Area(m), 6);
    }

    [Fact]
    public void AFaceWithNoLoopIsItsSurfaceOverItsDomain()
    {
        // A plane whose parameters run at twice its length (coeff_a 0.5), over the domain [0, 4] x [0, 6]: a 2 by 3 rectangle.
        var g = new Graph();
        var plane = new PrcSurf
        {
            Id = new PrcSurfPlane { Transform = Frame(new PrcVector(0, 0, 1), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0)), UParameterCoeffA = 0.5, UParameterCoeffB = 0, VParameterCoeffA = 0.5, VParameterCoeffB = 0 },
            IdType = PrcType.PRC_TYPE_SURF_Plane,
        };
        PrcPtrTopology connex = g.Store(PrcType.PRC_TYPE_TOPO_Connex, () => new PrcTopoConnex
        {
            Shells =
            [
                g.Store(PrcType.PRC_TYPE_TOPO_Shell, () => new PrcTopoShell
                {
                    IsClosed = false,
                    Faces =
                    [
                        new PrcFacesInShell
                        {
                            Face = g.Store(PrcType.PRC_TYPE_TOPO_Face, () => new PrcTopoFace
                            {
                                SurfaceGeometry = new PrcPtrSurface { IsReferenced = false, Surface = plane, SurfaceIdentifier = 0 },
                                IsTrimmed = true,
                                TrimmedSurface = new PrcDomain { MinUv = new PrcVector2D { X = 0, Y = 0 }, MaxUv = new PrcVector2D { X = 4, Y = 6 } },
                                IndexOfOuterLoop = -1,
                                Loops = [],
                            }),
                            Orientation = 1,
                        },
                    ],
                }),
            ],
        });

        var m = Tessellate(Convert(Context(Body(connex))));

        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        Assert.Equal(6, Area(m), 6);
        Assert.All(m.Points, p => Assert.True(p.X is >= -1e-6 and <= 2 + 1e-6 && p.Y is >= -1e-6 and <= 3 + 1e-6 && Math.Abs(p.Z - 1) < 1e-6));
    }

    [Fact]
    public void AnEdgeWithNoCurveInSpaceIsTheCoedgesCurveOnTheSurface()
    {
        // A square on a plane whose parameters run at twice its length: its edges have no 3D curve, each coedge a 2D line (a
        // B-spline of degree 1) in the plane's parameters, from (0, 0) to (4, 4): in space a 2 by 2 square, area 4.
        var g = new Graph();
        var plane = new PrcSurf
        {
            Id = new PrcSurfPlane { Transform = Frame(default, new PrcVector(1, 0, 0), new PrcVector(0, 1, 0)), UParameterCoeffA = 0.5, UParameterCoeffB = 0, VParameterCoeffA = 0.5, VParameterCoeffB = 0 },
            IdType = PrcType.PRC_TYPE_SURF_Plane,
        };
        (double U, double V)[] uv = [(0, 0), (4, 0), (4, 4), (0, 4)];
        var vertices = new object?[4];
        PrcPtrTopology Vertex(int i) => g.Use(ref vertices[i], PrcType.PRC_TYPE_TOPO_UniqueVertex, () => new PrcTopoUniqueVertex { Vertex = Vec(new PrcVector(uv[i].U / 2, uv[i].V / 2, 0)) });
        PrcPtrCurve Line2D(int a, int b) => Inline(new PrcCrvNurbs
        {
            CurveData = new PrcContentCurve { Is3dFlag = false },
            IsRational = false,
            Degree = 1,
            P = [new PrcControlPointsNurbsCrv { X = uv[a].U, Y = uv[a].V, Z = 0, W = 0 }, new PrcControlPointsNurbsCrv { X = uv[b].U, Y = uv[b].V, Z = 0, W = 0 }],
            U = [0, 0, 1, 1],
        }, PrcType.PRC_TYPE_CRV_NURBS);
        PrcPtrCurve none = Inline(null!, PrcType.PRC_TYPE_ROOT);
        PrcPtrTopology connex = g.Store(PrcType.PRC_TYPE_TOPO_Connex, () => new PrcTopoConnex
        {
            Shells =
            [
                g.Store(PrcType.PRC_TYPE_TOPO_Shell, () => new PrcTopoShell
                {
                    IsClosed = false,
                    Faces =
                    [
                        new PrcFacesInShell
                        {
                            Face = g.Store(PrcType.PRC_TYPE_TOPO_Face, () => new PrcTopoFace
                            {
                                SurfaceGeometry = new PrcPtrSurface { IsReferenced = false, Surface = plane, SurfaceIdentifier = 0 },
                                IsTrimmed = false,
                                TrimmedSurface = null,
                                IndexOfOuterLoop = 0,
                                Loops =
                                [
                                    g.Store(PrcType.PRC_TYPE_TOPO_Loop, () => new PrcTopoLoop
                                    {
                                        LoopOrientation = 1,
                                        Coedge = [.. Enumerable.Range(0, 4).Select(k => new PrcCoedgeInLoop
                                        {
                                            NextCoedge = g.Store(PrcType.PRC_TYPE_TOPO_CoEdge, () => new PrcTopoCoEdge
                                            {
                                                PtrTopology = g.Store(PrcType.PRC_TYPE_TOPO_Edge, () => new PrcTopoEdge
                                                {
                                                    WireEdge = new PrcContentWireEdge { PtrCurve = none, IsTrimmed = false, TrimInterval = default },
                                                    StartVertex = Vertex(k),
                                                    EndVertex = Vertex((k + 1) % 4),
                                                }),
                                                PtrCurves = Line2D(k, (k + 1) % 4),
                                                CoedgeOrientation = 1,
                                                UvOrientation = 1,
                                            }),
                                            NeighborIndex = 0,
                                        })],
                                    }),
                                ],
                            }),
                            Orientation = 1,
                        },
                    ],
                }),
            ],
        });

        PrcTopoBrepDataCompress body = Convert(Context(Body(connex)));

        Assert.All(body.Curves, c => Assert.Equal(PrcCurveKind.NurbsCurve, c.Kind));
        var m = Tessellate(body);
        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        Assert.Equal(4, Area(m), 6);
    }

    [Fact]
    public void ACylinderFaceWithNoLoopNorDomainIsTheCylinderOverItsSurfacesDomain()
    {
        // Asymptote writes tubes so: a cylinder face with no loop and no domain of its own; the surface's parameterisation keeps
        // [0, 2π] x [0, 5]. The domain's bottom and top sides are whole circles: the loop must not go round twice.
        var g = new Graph();
        var wall = new PrcSurf { Id = new PrcSurfCylinder { Transform = new PrcTransformation3DWithBit { Transformation = null }, Parameterization = Identity, Radius = 2 }, IdType = PrcType.PRC_TYPE_SURF_Cylinder };
        PrcPtrTopology connex = g.Store(PrcType.PRC_TYPE_TOPO_Connex, () => new PrcTopoConnex
        {
            Shells =
            [
                g.Store(PrcType.PRC_TYPE_TOPO_Shell, () => new PrcTopoShell
                {
                    IsClosed = false,
                    Faces =
                    [
                        new PrcFacesInShell
                        {
                            Face = g.Store(PrcType.PRC_TYPE_TOPO_Face, () => new PrcTopoFace
                            {
                                SurfaceGeometry = new PrcPtrSurface { IsReferenced = false, Surface = wall, SurfaceIdentifier = 0 },
                                IsTrimmed = false,
                                TrimmedSurface = null,
                                IndexOfOuterLoop = -1,
                                Loops = [],
                            }),
                            Orientation = 1,
                        },
                    ],
                }),
            ],
        });

        var m = Tessellate(Convert(Context(Body(connex))));

        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        double exact = 2 * Math.PI * 2 * 5;
        Assert.InRange(Area(m), exact * 0.98, exact * 1.001);
    }

    [Fact]
    public void AFaceListedOverAndOverByNumberIsConvertedOnce()
    {
        // A hostile file can name one stored face a million times for three bytes each: it is one face.
        PrcTopoContext context = Cube(new Graph());
        var data = (PrcTopoBrepData)context.Bodies[0].Id!;
        var shell = (PrcTopoShell)((PrcTopoConnex)data.Connex[0].Topo!.Id!).Shells[0].Topo!.Id!;
        uint firstFace = 3;     // connex 1, shell 2, the first face 3
        var many = new PrcTopoShell
        {
            IsClosed = true,
            Faces = [.. shell.Faces, .. Enumerable.Repeat(new PrcFacesInShell { Face = new PrcPtrTopology { IsStored = true, Topo = null, TopoIdentifier = firstFace }, Orientation = 1 }, 1_000_000)],
        };
        ((PrcTopoConnex)data.Connex[0].Topo!.Id!).Shells[0] = new PrcPtrTopology { IsStored = false, Topo = new PrcTopo { Id = many, IdType = PrcType.PRC_TYPE_TOPO_Shell }, TopoIdentifier = 0 };

        PrcTopoBrepDataCompress body = Convert(context);

        Assert.Equal(6, body.Faces.Length);
        Assert.Equal(24, body.Coedges.Length);
    }

    [Fact]
    public void ASplineSurfaceWithFewerPointsThanItsDegreeAsksFailsItsFacesNotTheFile()
    {
        // One point by two, degree 1 in u: no span. An edge with no 3D curve would evaluate it through the coedge's 2D curve.
        var g = new Graph();
        var bad = new PrcSurf
        {
            Id = new PrcSurfNurbs
            {
                IsRational = false, Du = 1, Dv = 1, HighestIndexOfControlPointsInU = 0, HighestIndexOfControlPointsInV = 1,
                P3 = [new PrcControlPointsNurbsSurf3 { X = 0, Y = 0, Z = 0 }, new PrcControlPointsNurbsSurf3 { X = 0, Y = 1, Z = 0 }],
                P4 = [], Ku = [0, 0, 1], Kv = [0, 0, 1, 1],
            },
            IdType = PrcType.PRC_TYPE_SURF_NURBS,
        };
        object? v = null;
        PrcPtrCurve uv = Inline(new PrcCrvNurbs
        {
            CurveData = new PrcContentCurve { Is3dFlag = false }, IsRational = false, Degree = 1,
            P = [new PrcControlPointsNurbsCrv { X = 0, Y = 0, Z = 0, W = 0 }, new PrcControlPointsNurbsCrv { X = 0, Y = 1, Z = 0, W = 0 }],
            U = [0, 0, 1, 1],
        }, PrcType.PRC_TYPE_CRV_NURBS);
        PrcPtrTopology connex = g.Store(PrcType.PRC_TYPE_TOPO_Connex, () => new PrcTopoConnex
        {
            Shells =
            [
                g.Store(PrcType.PRC_TYPE_TOPO_Shell, () => new PrcTopoShell
                {
                    IsClosed = false,
                    Faces =
                    [
                        new PrcFacesInShell
                        {
                            Face = g.Store(PrcType.PRC_TYPE_TOPO_Face, () => new PrcTopoFace
                            {
                                SurfaceGeometry = new PrcPtrSurface { IsReferenced = false, Surface = bad, SurfaceIdentifier = 0 },
                                IsTrimmed = false, TrimmedSurface = null, IndexOfOuterLoop = 0,
                                Loops =
                                [
                                    g.Store(PrcType.PRC_TYPE_TOPO_Loop, () => new PrcTopoLoop
                                    {
                                        LoopOrientation = 1,
                                        Coedge =
                                        [
                                            new PrcCoedgeInLoop
                                            {
                                                NextCoedge = g.Store(PrcType.PRC_TYPE_TOPO_CoEdge, () => new PrcTopoCoEdge
                                                {
                                                    PtrTopology = g.Store(PrcType.PRC_TYPE_TOPO_Edge, () => new PrcTopoEdge
                                                    {
                                                        WireEdge = new PrcContentWireEdge { PtrCurve = Inline(null!, PrcType.PRC_TYPE_ROOT), IsTrimmed = false, TrimInterval = default },
                                                        StartVertex = g.Use(ref v, PrcType.PRC_TYPE_TOPO_UniqueVertex, () => new PrcTopoUniqueVertex { Vertex = Vec(default) }),
                                                        EndVertex = g.Use(ref v, PrcType.PRC_TYPE_TOPO_UniqueVertex, () => throw new InvalidOperationException()),
                                                    }),
                                                    PtrCurves = uv, CoedgeOrientation = 1, UvOrientation = 1,
                                                }),
                                                NeighborIndex = 0,
                                            },
                                        ],
                                    }),
                                ],
                            }),
                            Orientation = 1,
                        },
                    ],
                }),
            ],
        });

        PrcTopoContext context = Context(Body(connex));
        bool converted = PrcBrepDataConverter.TryConvert((PrcTopoBrepData)context.Bodies[0].Id!, PrcBrepDataConverter.Index(context), 1e-6, out PrcTopoBrepDataCompress body, out _);

        // Either the body converts with the face left to fail alone, or the body fails: never an exception out of the converter.
        if (converted)
        {
            using var builder = new SceneBuilder(SceneLimits.Default);
            PrcBrepTessellator.AddMesh(builder, body, builder.AddName("x"), out PrcBrepTessellation result, cancellation: TestContext.Current.CancellationToken);
            Assert.Equal(0, result.Triangles);
        }
    }

    [Fact]
    public void AnIntersectionEdgeIsItsCrossingPointsAndItsSurfacesTakeNumbers()
    {
        // A 2 by 2 square in z = 0 whose top edge is an intersection curve through (2, 2), (1, 2.5), (0, 2): a bulge of the square. The
        // intersection holds two surfaces of its own, which take numbers after the face's plane: 1 the face's, 2 and 3 the
        // intersection's.
        var g = new Graph();
        PrcSurf Plane(double z) => new() { Id = new PrcSurfPlane { Transform = Frame(new PrcVector(0, 0, z), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0)), UParameterCoeffA = 1, UParameterCoeffB = 0, VParameterCoeffA = 1, VParameterCoeffB = 0 }, IdType = PrcType.PRC_TYPE_SURF_Plane };
        PrcVector[] corners = [new(0, 0, 0), new(2, 0, 0), new(2, 2, 0), new(0, 2, 0)];
        var vertices = new object?[4];
        PrcPtrTopology Vertex(int i) => g.Use(ref vertices[i], PrcType.PRC_TYPE_TOPO_UniqueVertex, () => new PrcTopoUniqueVertex { Vertex = Vec(corners[i]) });
        PrcPtrCurve EdgeCurve(int k) => k != 2 ? Inline(Line(), PrcType.PRC_TYPE_CRV_Line) : Inline(new PrcCrvIntersection
        {
            CurveData = new PrcContentCurve { Is3dFlag = true },
            Transform3d = null,
            Surface1 = new PrcPtrSurface { IsReferenced = false, Surface = Plane(5), SurfaceIdentifier = 0 },
            Surface2 = new PrcPtrSurface { IsReferenced = false, Surface = Plane(1), SurfaceIdentifier = 0 },
            Crossings =
            [
                new PrcCrossingPointsCrvIntersection { Position = Vec(new PrcVector(2, 2, 0)), Parameter = 0 },
                new PrcCrossingPointsCrvIntersection { Position = Vec(new PrcVector(1, 2.5, 0)), Parameter = 1 },
                new PrcCrossingPointsCrvIntersection { Position = Vec(new PrcVector(0, 2, 0)), Parameter = 2 },
            ],
        }, PrcType.PRC_TYPE_CRV_Intersection);
        PrcPtrTopology Face(PrcPtrSurface surface) => g.Store(PrcType.PRC_TYPE_TOPO_Face, () => new PrcTopoFace
        {
            SurfaceGeometry = surface,
            IsTrimmed = false,
            TrimmedSurface = null,
            IndexOfOuterLoop = 0,
            Loops =
            [
                g.Store(PrcType.PRC_TYPE_TOPO_Loop, () => new PrcTopoLoop
                {
                    LoopOrientation = 1,
                    Coedge = [.. Enumerable.Range(0, 4).Select(k => new PrcCoedgeInLoop
                    {
                        NextCoedge = g.Store(PrcType.PRC_TYPE_TOPO_CoEdge, () => new PrcTopoCoEdge
                        {
                            PtrTopology = g.Store(PrcType.PRC_TYPE_TOPO_Edge, () => new PrcTopoEdge
                            {
                                WireEdge = new PrcContentWireEdge { PtrCurve = EdgeCurve(k), IsTrimmed = false, TrimInterval = default },
                                StartVertex = Vertex(k),
                                EndVertex = Vertex((k + 1) % 4),
                            }),
                            PtrCurves = NoCurve,
                            CoedgeOrientation = 1,
                            UvOrientation = 1,
                        }),
                        NeighborIndex = 0,
                    })],
                }),
            ],
        });
        PrcPtrTopology connex = g.Store(PrcType.PRC_TYPE_TOPO_Connex, () => new PrcTopoConnex
        {
            Shells =
            [
                g.Store(PrcType.PRC_TYPE_TOPO_Shell, () => new PrcTopoShell
                {
                    IsClosed = false,
                    Faces = [new PrcFacesInShell { Face = Face(new PrcPtrSurface { IsReferenced = false, Surface = Plane(0), SurfaceIdentifier = 0 }), Orientation = 1 }],
                }),
            ],
        });

        PrcTopoContext context = Context(Body(connex));
        PrcBrepDataConverter.Tables tables = PrcBrepDataConverter.Index(context);
        var m = Tessellate(Convert(context));

        Assert.Equal(3, tables.Surfaces.Count);
        Assert.Equal(1.0, ((PrcSurfPlane)tables.Surfaces[2].Id!).Transform.Translation.Z);
        Assert.Equal((1, 0), (m.Result.Faces, m.Result.FacesDropped));
        Assert.Equal(4 + 0.5, Area(m), 6);       // the square and the triangle the crossing points make above it
    }

    [Fact]
    public void ANumberNoEntityWasGivenFailsTheBody()
    {
        PrcTopoContext context = Cube(new Graph());
        var data = (PrcTopoBrepData)context.Bodies[0].Id!;
        var broken = new PrcTopoBrepData { Connex = [.. data.Connex, new PrcPtrTopology { IsStored = true, Topo = null, TopoIdentifier = 9999 }] };
        var bad = Context(new PrcTopoBody { Id = broken, IdType = PrcType.PRC_TYPE_TOPO_BrepData });

        Assert.False(PrcBrepDataConverter.TryConvert(broken, PrcBrepDataConverter.Index(bad), 1e-6, out _, out string? error));
        Assert.Contains("9999", error);
    }

    [Fact]
    public void ANumberCountsOnAcrossTheBodiesOfAContext()
    {
        // Two cubes in one context: the second's references name numbers after the first's.
        var g = new Graph();
        PrcTopoContext first = Cube(g), second = Cube(g);
        PrcTopoContext both = Context(first.Bodies[0], second.Bodies[0]);

        PrcTopoBrepDataCompress body = Convert(both, 1);

        Assert.Equal(6, Tessellate(body).Result.Faces);
        Assert.Equal(8, body.Vertices.Length);
    }

    [Fact]
    public void ACylinderBetweenTwoWholeCircleEdgesIsItsWallAndItsEnds()
    {
        // Radius 2, height 5, about z: the wall (one face of two loops) and two discs, the circles shared by number.
        var g = new Graph();
        object? bottomVertex = null, topVertex = null, bottomEdge = null, topEdge = null;
        PrcPtrTopology CircleEdge(ref object? edge, ref object? vertex, double z)
        {
            object? v = vertex;
            PrcPtrTopology pointer = g.Use(ref edge, PrcType.PRC_TYPE_TOPO_Edge, () =>
            {
                PrcPtrTopology start = g.Use(ref v, PrcType.PRC_TYPE_TOPO_UniqueVertex, () => new PrcTopoUniqueVertex { Vertex = Vec(new PrcVector(2, 0, z)) });
                PrcPtrTopology end = g.Use(ref v, PrcType.PRC_TYPE_TOPO_UniqueVertex, () => throw new InvalidOperationException());
                var circle = new PrcCrvCircle
                {
                    CurveData = new PrcContentCurve { Is3dFlag = true },
                    Transform3d = new PrcTransformation3DWithBit { Transformation = Frame(new PrcVector(0, 0, z), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0)) },
                    Parameterization = Parameters(0, 2 * Math.PI),
                    Radius = 2,
                };
                return new PrcTopoEdge { WireEdge = new PrcContentWireEdge { PtrCurve = Inline(circle, PrcType.PRC_TYPE_CRV_Circle), IsTrimmed = false, TrimInterval = default }, StartVertex = start, EndVertex = end };
            });
            vertex = v;
            return pointer;
        }

        PrcPtrTopology Loop(Func<PrcPtrTopology> edge) => g.Store(PrcType.PRC_TYPE_TOPO_Loop, () => new PrcTopoLoop
        {
            LoopOrientation = 1,
            Coedge = [new PrcCoedgeInLoop { NextCoedge = g.Store(PrcType.PRC_TYPE_TOPO_CoEdge, () => new PrcTopoCoEdge { PtrTopology = edge(), PtrCurves = NoCurve, CoedgeOrientation = 1, UvOrientation = 1 }), NeighborIndex = 0 }],
        });

        PrcPtrTopology Face(PrcSurf surface, params Func<PrcPtrTopology>[] loops) => g.Store(PrcType.PRC_TYPE_TOPO_Face, () => new PrcTopoFace
        {
            SurfaceGeometry = new PrcPtrSurface { IsReferenced = false, Surface = surface, SurfaceIdentifier = 0 },
            IsTrimmed = false,
            TrimmedSurface = null,
            IndexOfOuterLoop = 0,
            Loops = [.. loops.Select(Loop)],
        });

        var wall = new PrcSurf { Id = new PrcSurfCylinder { Transform = new PrcTransformation3DWithBit { Transformation = null }, Parameterization = Identity, Radius = 2 }, IdType = PrcType.PRC_TYPE_SURF_Cylinder };
        PrcSurf Disc(double z) => new() { Id = new PrcSurfPlane { Transform = Frame(new PrcVector(0, 0, z), new PrcVector(1, 0, 0), new PrcVector(0, 1, 0)), UParameterCoeffA = 1, UParameterCoeffB = 0, VParameterCoeffA = 1, VParameterCoeffB = 0 }, IdType = PrcType.PRC_TYPE_SURF_Plane };
        PrcPtrTopology connex = g.Store(PrcType.PRC_TYPE_TOPO_Connex, () => new PrcTopoConnex
        {
            Shells =
            [
                g.Store(PrcType.PRC_TYPE_TOPO_Shell, () => new PrcTopoShell
                {
                    IsClosed = true,
                    Faces =
                    [
                        new PrcFacesInShell { Face = Face(wall, () => CircleEdge(ref bottomEdge, ref bottomVertex, 0), () => CircleEdge(ref topEdge, ref topVertex, 5)), Orientation = 1 },
                        new PrcFacesInShell { Face = Face(Disc(0), () => CircleEdge(ref bottomEdge, ref bottomVertex, 0)), Orientation = 0 },
                        new PrcFacesInShell { Face = Face(Disc(5), () => CircleEdge(ref topEdge, ref topVertex, 5)), Orientation = 1 },
                    ],
                }),
            ],
        });

        PrcTopoBrepDataCompress body = Convert(Context(Body(connex)));

        Assert.Equal(2, body.Curves.Length);
        var m = Tessellate(body);
        Assert.Equal((3, 0), (m.Result.Faces, m.Result.FacesDropped));
        double exact = (2 * Math.PI * 2 * 5) + (2 * Math.PI * 4);
        Assert.InRange(Area(m), exact * 0.98, exact * 1.001);
        Assert.InRange(Math.Abs(Volume(m)), Math.PI * 4 * 5 * 0.98, Math.PI * 4 * 5 * 1.001);
    }
}
