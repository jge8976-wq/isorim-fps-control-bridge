using UnityEngine;

namespace HSK.FpsBridge
{
    internal static class RatkinGearPose
    {
        // Captured sprite pose -> true cell space -> one scale -> drawn space.
        internal static Matrix4x4 Equipment(Matrix4x4 matrix, float squash, Vector3 center, float length)
        {
            Matrix4x4 upright = Matrix4x4.Scale(new Vector3(1f, squash, 1f)) * matrix;
            float fit = Mathf.Max(((Vector3)upright.GetColumn(0)).magnitude, ((Vector3)upright.GetColumn(1)).magnitude);
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 basis = ((Vector3)upright.GetColumn(axis)).normalized * fit;
                upright.SetColumn(axis, new Vector4(basis.x, basis.y, basis.z, 0f));
            }
            Matrix4x4 model = Matrix4x4.Rotate(Quaternion.Euler(0f, 90f, 0f))
                * Matrix4x4.Scale(Vector3.one * (.8f / Mathf.Max(.05f, length))) * Matrix4x4.Translate(-center);
            return Matrix4x4.Scale(new Vector3(1f, 1f / squash, 1f)) * upright * model;
        }
        internal static Matrix4x4 Shield(Vector3 min, Vector3 max, float aim, float sprint, bool moving, float time)
        {
            Vector3 center = (min + max) * .5f;
            float scale = Mathf.Min(1f, .46f / Mathf.Max(.05f, max.y - min.y));
            Vector3 at = new Vector3(-.27f - .06f * aim, -.23f - .15f * aim - .03f * sprint, .44f - .04f * sprint);
            if (moving) at += new Vector3(Mathf.Sin(time * 8f) * .004f, Mathf.Abs(Mathf.Cos(time * 8f)) * .006f, 0f);
            return Matrix4x4.TRS(at, Quaternion.Euler(5f + 12f * sprint, -18f, -8f), Vector3.one * scale)
                * Matrix4x4.Translate(-center);
        }
    }
}
