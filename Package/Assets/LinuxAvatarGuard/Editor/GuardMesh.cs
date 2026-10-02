// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;

namespace LinuxAvatarGuard
{
    public static class GuardMesh
    {
        public static int[] NewKey()
        {
            var b = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            // Avoid all-zero coefficients and preserve byte transport over OSC.
            return Array.ConvertAll(b, x => 32 + x % 224);
        }

        public static float Offset(Vector4 coefficients, int[] key)
        {
            return (coefficients.x * key[0] + coefficients.y * key[1] +
                    coefficients.z * key[2] + coefficients.w * key[3]) / 255f;
        }

        public static void ValidateInputs(Mesh source, int[] key, float strength)
        {
            if (source == null || !source.isReadable) throw new InvalidOperationException("La malla debe ser legible. No se cambia su importador automáticamente.");
            if (key == null || key.Length != 4 || Array.Exists(key, x => x < 0 || x > 255)) throw new ArgumentException("Clave inválida.");
            if ((float.IsNaN(strength) || float.IsInfinity(strength)) || strength <= 0 || strength > 0.5f) throw new ArgumentException("Intensidad fuera de rango (0, 0.5].");
            var uv = new List<Vector4>();
            source.GetUVs(6, uv);
            if (uv.Count != 0) throw new InvalidOperationException("UV7 ocupado: se conserva el original y se cancela.");
            source.GetUVs(7, uv);
            if (uv.Count != 0) throw new InvalidOperationException("UV8 ocupado: se conserva el original y se cancela.");
            var vertices = source.vertices;
            var normals = source.normals;
            if (vertices.Length == 0 || normals.Length != vertices.Length) throw new InvalidOperationException("Malla vacía o sin normales.");
            for (int i = 0; i < vertices.Length; i++)
                if (Mathf.Abs(normals[i].magnitude - 1) > 0.002f) throw new InvalidOperationException("Las normales deben ser unitarias.");
            // Changing normal deltas changes the decoder direction after blendshape normalization.
            var dv = new Vector3[vertices.Length]; var dn = new Vector3[vertices.Length]; var dt = new Vector3[vertices.Length];
            for (int b = 0; b < source.blendShapeCount; b++)
                for (int f = 0; f < source.GetBlendShapeFrameCount(b); f++)
                {
                    source.GetBlendShapeFrameVertices(b, f, dv, dn, dt);
                    for (int i = 0; i < dn.Length; i++)
                        if (dn[i].sqrMagnitude > 1e-12f) throw new InvalidOperationException("Blendshape con deltas de normales: no es compatible con este decodificador conservador. No se elimina ni se modifica el blendshape.");
                }
        }

        public static Mesh Encode(Mesh source, int[] key, float strength)
        {
            ValidateInputs(source, key, strength);
            var vertices = source.vertices;
            var normals = source.normals;
            var uv6 = new List<Vector2>(vertices.Length); var uv7 = new List<Vector2>(vertices.Length);
            var bytes = new byte[vertices.Length * 4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            for (int i = 0; i < vertices.Length; i++)
            {
                var c = new Vector4(bytes[i*4]/127.5f-1, bytes[i*4+1]/127.5f-1, bytes[i*4+2]/127.5f-1, bytes[i*4+3]/127.5f-1) * strength;
                vertices[i] += normals[i] * Offset(c, key);
                uv6.Add(new Vector2(c.x, c.y)); uv7.Add(new Vector2(c.z, c.w));
            }
            var result = UnityEngine.Object.Instantiate(source);
            result.name = source.name + "_LAG";
            result.vertices = vertices;
            result.SetUVs(6, uv6); result.SetUVs(7, uv7);
            // Preserve normals, tangents, blendshapes, bindposes, indices and original bounds.
            result.bounds = source.bounds;
            return result;
        }
    }
}
