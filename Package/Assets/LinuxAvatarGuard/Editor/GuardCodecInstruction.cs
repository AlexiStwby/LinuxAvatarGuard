// SPDX-License-Identifier: MIT
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace LinuxAvatarGuard
{
    // Closed, immutable IR. No shader text or runtime keys are accepted as instruction arguments.
    public sealed class CodecInstruction
    {
        public CodecOperationKind Kind { get; }
        public int Version => 1;
        public int Axis { get; }
        public int OtherAxis { get; }
        public float Factor { get; }
        public Vector4 RowX { get; }
        public Vector4 RowY { get; }
        public Vector4 RowZ { get; }
        CodecInstruction(CodecOperationKind kind, int axis = 0, int other = 1, float factor = 0,
            Vector4 x = default, Vector4 y = default, Vector4 z = default)
        {
            Kind = kind; Axis = axis; OtherAxis = other; Factor = factor;
            RowX = x; RowY = y; RowZ = z;
            Validate();
        }
        public static CodecInstruction Swap(int a, int b) => new CodecInstruction(CodecOperationKind.AxisSwap, a, b);
        public static CodecInstruction Flip(int a) => new CodecInstruction(CodecOperationKind.AxisFlip, a);
        public static CodecInstruction Shear(int a, int b, float factor) => new CodecInstruction(CodecOperationKind.TriangularShear, a, b, factor);
        public static CodecInstruction Bend(int a, int b, float factor) => new CodecInstruction(CodecOperationKind.BoundedBend, a, b, factor);
        public static CodecInstruction Offset(Vector4 x, Vector4 y, Vector4 z) =>
            new CodecInstruction(CodecOperationKind.KeyedVectorOffset, x: x, y: y, z: z);
        public void Validate()
        {
            if (Axis < 0 || Axis > 2 || OtherAxis < 0 || OtherAxis > 2)
                throw new ArgumentOutOfRangeException("axis");
            switch (Kind)
            {
                case CodecOperationKind.AxisSwap:
                    if (Axis == OtherAxis) throw new ArgumentException("Swap axes must be distinct.");
                    break;
                case CodecOperationKind.AxisFlip: break;
                case CodecOperationKind.TriangularShear:
                case CodecOperationKind.BoundedBend:
                    if (Axis == OtherAxis || !Finite(Factor) || Mathf.Abs(Factor) > .25f || Factor == 0)
                        throw new ArgumentException("Triangular operations require distinct axes and a finite nonzero factor within +/-0.25.");
                    break;
                case CodecOperationKind.KeyedVectorOffset:
                    for (int i = 0; i < 4; i++)
                        if (!Finite(RowX[i]) || !Finite(RowY[i]) || !Finite(RowZ[i]) ||
                            Mathf.Abs(RowX[i]) > 1 || Mathf.Abs(RowY[i]) > 1 || Mathf.Abs(RowZ[i]) > 1)
                            throw new ArgumentException("Offset coefficients must be finite and within +/-1.");
                    // Gram determinant: reject a degenerate displacement subspace.
                    float xx = Vector4.Dot(RowX, RowX), yy = Vector4.Dot(RowY, RowY), zz = Vector4.Dot(RowZ, RowZ);
                    float xy = Vector4.Dot(RowX, RowY), xz = Vector4.Dot(RowX, RowZ), yz = Vector4.Dot(RowY, RowZ);
                    float determinant = xx * (yy * zz - yz * yz) - xy * (xy * zz - yz * xz) + xz * (xy * yz - yy * xz);
                    if (!Finite(determinant) || determinant < .1f) throw new ArgumentException("Offset rows must span three dimensions.");
                    break;
                default: throw new ArgumentException("Unsupported static codec instruction.");
            }
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal Vector3 Apply(Vector3 position, Vector4 payload, Vector4 key, bool inverse)
        {
            float sign = inverse ? -1 : 1;
            switch (Kind)
            {
                case CodecOperationKind.AxisSwap:
                    float temp = position[Axis]; position[Axis] = position[OtherAxis]; position[OtherAxis] = temp;
                    break;
                case CodecOperationKind.AxisFlip: position[Axis] = -position[Axis]; break;
                case CodecOperationKind.TriangularShear: position[Axis] += sign * Factor * position[OtherAxis]; break;
                case CodecOperationKind.BoundedBend:
                    float value = position[OtherAxis];
                    position[Axis] += sign * Factor * (value / (1 + Mathf.Abs(value)));
                    break;
                case CodecOperationKind.KeyedVectorOffset:
                    var weighted = Vector4.Scale(payload, key);
                    position += sign * new Vector3(Vector4.Dot(RowX, weighted), Vector4.Dot(RowY, weighted), Vector4.Dot(RowZ, weighted));
                    break;
            }
            return position;
        }
        static string F(float value)
        {
            string result = value.ToString("R", CultureInfo.InvariantCulture);
            return result.IndexOf('.') >= 0 || result.IndexOf('E') >= 0 || result.IndexOf('e') >= 0 ? result : result + ".0";
        }
        static string V(Vector4 value) => "float4(" + F(value.x) + "," + F(value.y) + "," + F(value.z) + "," + F(value.w) + ")";
        internal void EmitInverse(StringBuilder output)
        {
            char a = "xyz"[Axis], b = "xyz"[OtherAxis];
            switch (Kind)
            {
                case CodecOperationKind.AxisSwap:
                    output.Append("{ float t=p.").Append(a).Append("; p.").Append(a).Append("=p.").Append(b).Append("; p.").Append(b).Append("=t; }\n");
                    break;
                case CodecOperationKind.AxisFlip: output.Append("p.").Append(a).Append("=-p.").Append(a).Append(";\n"); break;
                case CodecOperationKind.TriangularShear:
                    output.Append("p.").Append(a).Append("-=(").Append(F(Factor)).Append(")*p.").Append(b).Append(";\n");
                    break;
                case CodecOperationKind.BoundedBend:
                    output.Append("p.").Append(a).Append("-=(").Append(F(Factor)).Append(")*(p.").Append(b).Append("/(1.0+abs(p.").Append(b).Append(")));\n");
                    break;
                case CodecOperationKind.KeyedVectorOffset:
                    output.Append("p-=float3(dot(").Append(V(RowX)).Append(",c*k),dot(").Append(V(RowY))
                        .Append(",c*k),dot(").Append(V(RowZ)).Append(",c*k));\n");
                    break;
            }
        }
        internal void WriteCanonical(BinaryWriter output)
        {
            output.Write((int)Kind); output.Write(Version); output.Write(Axis); output.Write(OtherAxis); output.Write(Factor);
            foreach (var row in new[] { RowX, RowY, RowZ }) for (int i = 0; i < 4; i++) output.Write(row[i]);
        }
    }
}
