/*
 * This confidential and proprietary software may be used only as
 * authorised by a licensing agreement from ARM Limited
 * (C) COPYRIGHT 2021 ARM Limited
 * ALL RIGHTS RESERVED
 * The entire notice above must be reproduced on all authorised
 * copies and copies may only be made to the extent permitted
 * by a licensing agreement from ARM Limited.
 */

using System.Collections.Generic;
using UnityEngine;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Unity.Burst.Intrinsics.Arm.Neon;
using Unity.Collections.LowLevel.Unsafe;
using System.Diagnostics;
using UnityEngine.UI;
using System.Runtime.CompilerServices;

public struct DynamicCollisionObject
{
    public float2 position;
    public float radius;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Intersects(DynamicCollisionObject other)
    {
        float distance = radius + other.radius;
        return math.lengthsq(other.position - position) < distance * distance;
    }
}

public struct StaticCollisionObject
{
    public float2 minPos;
    public float2 maxPos;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Intersects(StaticCollisionObject other)
    {
        return !(other.minPos.x > maxPos.x
              || other.maxPos.x < minPos.x
              || other.minPos.y > maxPos.y
              || other.maxPos.y < minPos.y);
    }
}

[BurstCompile]
public class CollisionCalculationScript : MonoBehaviour
{
    public const int maxCharacters = 2401;//sum of spawning script's numToSpawn must be less than this!!
    public const float DEFAULT_RADIUS = 0.5f;

    const float fpsMeasurePeriod = 0.5f;
    private int m_FpsAccumulator = 0;
    private float m_FpsNextPeriod = 0;
    private int m_CurrentFps;
    const string display = "{0} FPS";

    public enum Mode
    {
        Plain,
        Burst,
        Neon
    }
    // The `codeMode` determines which version of the code is used. 
    // Plain code mode just uses the standard toolchain. 
    // Burst code mode uses Burst to increase performance. 
    // Neon code mode uses Neon intrinsics to maximize performance. 
    public const Mode codeMode = Mode.Burst; 
    private const bool useJobs = true; // The initial implementation with Jobs, kept at the bottom of file for reference. Jobs are wanted in a real game as async is useful, but affect ability to time improvements.
    private const bool unrolled = true; // The second implementation before unrolling is also kept for reference. Speed improvement, but not as fast as unrolled code which increases vectorisation.

    //walls data filled once on start
    // array of wall objects (ie the static, unmoving objects)
    private NativeArray<StaticCollisionObject> staticObjects;
    //data array of wall positions for Neon AABB collisions: please refer to Arm's Neon Burst in Unity document for explanations
    private float[] staticFloats;//[min.x, min.y, -max.x, -max.y] for each static object => laid out so there is one comparison for each object, where they are all the same > operation.
    private int numWalls;

    //character (ie the dynamic, moving objects) data re-filled each frame into fixed-length arrays 
    //data arrays of character positions for Neon Radius-based collisions
    private float[] dynamicPositionsX;
    private float[] dynamicPositionsY;
    private float[] dynamicRadii;
    //data array of character positions for Neon AABB collisions
    private float[] dynamicFloats;//[max.x, max.y, -min.x, -min.y] for each character => laid out to be in correct order to compare against the walls in the staticFloats array.
    // for plain implementation and original burst jobs
    private NativeArray<DynamicCollisionObject> dynamicObjects; 
    private NativeArray<StaticCollisionObject> dynamicObjectsVstatic;


    //collision result data
    private bool[] staticCollisions; //AABB collisions between characters and walls. This is a 2D array, of walls vs characters, so each character can build a list of which walls it intersects.
    private bool[] dynamicCollisions; //Radius-based collisions between characters. This is a 2D array, of characters vs characters for which collide with which, but only the upper triangle of the 2D array is used, rather than have data duplication.

    //collision result data if using jobs - not used by default
    private NativeArray<bool> staticCollisionsBurst; // layout as above bool[] arrays, but in Unity structure designed to work well with Jobs and be easier to use than a raw array.
    private NativeArray<bool> dynamicCollisionsBurst;

    //Timers for profiling sections of Update
    private const int TimerReset = 3601;//reset after this number of frames (after first 2min on Android) => Don't time initial period before stress, vectorisation advantage is for when there are lots of objects.
    private int frameNumber = 0;
    private int frameleftToReset = TimerReset;
    private Stopwatch dataSetupMs = new Stopwatch();
    private Stopwatch staticCollisionsMs = new Stopwatch();
    private Stopwatch dynamicCollisionsMs = new Stopwatch();
    private Stopwatch collisionMoveMs = new Stopwatch();
    private System.DateTime startTime;

    private Text UIText;

    //don't reallocate even small arrays each update if unneeded
    NativeArray<StaticCollisionObject> staticCollidingObjects;
    NativeArray<DynamicCollisionObject> dynamicCollidingObjects;
    GameObject[] dObjectList;

    public double setupFrameTime()
    {
        return dataSetupMs.Elapsed.TotalMilliseconds / frameNumber;
    }
    public double AABBCollideFrameTime()
    {
        if (frameNumber < TimerReset)//we reset after some time to only compare the big collision numbers
            return staticCollisionsMs.Elapsed.TotalMilliseconds / frameNumber;
        return staticCollisionsMs.Elapsed.TotalMilliseconds / (frameNumber - (TimerReset-1));
    }
    public double radiusCollideFrameTime()
    {
        if (frameNumber < TimerReset)//we reset after some time to only compare the big collision numbers
            return dynamicCollisionsMs.Elapsed.TotalMilliseconds / frameNumber;
        return dynamicCollisionsMs.Elapsed.TotalMilliseconds / (frameNumber - (TimerReset-1));
    }
    public double CollideMoveFrameTime()
    {
        return collisionMoveMs.Elapsed.TotalMilliseconds / frameNumber;
    }

    // Start is called before the first frame update
    public void Start()
    {
        m_FpsNextPeriod = Time.realtimeSinceStartup + fpsMeasurePeriod;

        startTime = System.DateTime.UtcNow;

        GameObject[] walls = GameObject.FindGameObjectsWithTag("wall");
        numWalls = walls.Length;
        staticObjects = new NativeArray<StaticCollisionObject>(numWalls, Allocator.Persistent);
        staticFloats = new float[numWalls * 4];

        int i = 0;
        foreach(GameObject gobj in walls)
        {
            Bounds wallBounds = gobj.GetComponent<Renderer>().bounds;
            float2 minB = new float2 ( wallBounds.min.x, wallBounds.min.z );
            float2 maxB = new float2(wallBounds.max.x, wallBounds.max.z);
            if (codeMode == Mode.Neon)
            {//Neon layout
                if (unrolled) // => true
                { // unrolled version is faster
                    staticFloats[i] = minB.x; staticFloats[numWalls + i] = minB.y;
                    staticFloats[2 * numWalls + i] = -maxB.x; staticFloats[3 * numWalls + i] = -maxB.y;//negative to enable ability to load everything into a batch greater than
                }
                else // => false (second implementation)
                { //before unrolling, data layout was:
                    staticFloats[i * 4] = minB.x; staticFloats[i * 4 + 1] = minB.y;
                    staticFloats[i * 4 + 2] = -maxB.x; staticFloats[i * 4 + 3] = -maxB.y;
                }
            }
            else if (codeMode == Mode.Burst)
            {
                staticFloats[i * 4] = minB.x; staticFloats[i * 4 + 1] = minB.y;
                staticFloats[i * 4 + 2] = maxB.x; staticFloats[i * 4 + 3] = maxB.y;//not negative or we just have to reverse them again for non-Neon implementation
            }
            staticObjects[i] = new StaticCollisionObject { minPos = minB, maxPos = maxB};
            i += 1;
        }

        dynamicPositionsX = new float[maxCharacters];
        dynamicPositionsY = new float[maxCharacters];
        dynamicRadii = new float[maxCharacters];
        dynamicFloats = new float[maxCharacters * 4];//[min.x, min.y, -max.x, -max.y] for each character

        staticCollisions = new bool[numWalls * maxCharacters];
        dynamicCollisions = new bool[maxCharacters * maxCharacters];

        UIText = FindObjectOfType<Text>();

        staticCollidingObjects = new NativeArray<StaticCollisionObject>(2, Allocator.Persistent);
        dynamicCollidingObjects = new NativeArray<DynamicCollisionObject>(4, Allocator.Persistent);
        dObjectList = new GameObject[4];
    }

    private void OnDestroy()
    {
        staticObjects.Dispose();
        staticCollidingObjects.Dispose();
        dynamicCollidingObjects.Dispose();
    }

    // Update is called once per frame
    public void Update()
    {
        ++frameNumber;
        --frameleftToReset;

        // measure average frames per second
        m_FpsAccumulator++;
        if (Time.realtimeSinceStartup > m_FpsNextPeriod)
        {
            m_CurrentFps = (int)(m_FpsAccumulator / fpsMeasurePeriod);
            m_FpsAccumulator = 0;
            m_FpsNextPeriod += fpsMeasurePeriod;
        }

        UnityEngine.Profiling.Profiler.BeginSample("CollisionDataSetup");
        dataSetupMs.Start();

        GameObject[] characters = GameObject.FindGameObjectsWithTag("player");
        int numChar = characters.Length;

        dynamicObjects = new NativeArray<DynamicCollisionObject>(maxCharacters, Allocator.TempJob);
        dynamicObjectsVstatic = new NativeArray<StaticCollisionObject>(maxCharacters, Allocator.TempJob);
        DoSetup(numChar, characters);

        dataSetupMs.Stop();
        UnityEngine.Profiling.Profiler.EndSample();

        // Static Wall AABB collision calculations
        UnityEngine.Profiling.Profiler.BeginSample("StaticCollisionCalculations");
        if (frameNumber == TimerReset)//final numbers only include those with lots of characters to collide
            staticCollisionsMs.Reset();
        staticCollisionsMs.Start();
        switch (codeMode)
        {
            case Mode.Plain:
                DoWallsPlain(numChar);
                 break;
            case Mode.Neon:
                DoWallsNeon(numChar);
                break;
            case Mode.Burst:
                DoWallsBurst(numChar);
                break;
        }
        staticCollisionsMs.Stop();
        UnityEngine.Profiling.Profiler.EndSample();

        UnityEngine.Profiling.Profiler.BeginSample("DynamicCollisionCalculations");
        if (frameNumber == TimerReset)//final numbers only include those with lots of characters to collide
            dynamicCollisionsMs.Reset();
        dynamicCollisionsMs.Start();
        switch (codeMode)
        {
            case Mode.Plain:
                DoCharactersPlain(numChar);
                break;
            case Mode.Neon:
                DoCharactersNeon(numChar);
                break;
            case Mode.Burst:
                DoCharactersBurst(numChar);
                break;
        }
        dynamicCollisionsMs.Stop();
        UnityEngine.Profiling.Profiler.EndSample();

        if (useJobs) // => false
        {//should just use these arrays to check below, but to integrate into other options, copy data to other array type
            if(staticCollisionsBurst.Length == staticCollisions.Length)
                staticCollisionsBurst.CopyTo(staticCollisions);
            if (dynamicCollisionsBurst.Length == dynamicCollisions.Length)
                dynamicCollisionsBurst.CopyTo(dynamicCollisions);
        }

        UnityEngine.Profiling.Profiler.BeginSample("Collision movement");
        collisionMoveMs.Start();
        CollisionMovement(numChar, characters);
        collisionMoveMs.Stop();
        UnityEngine.Profiling.Profiler.EndSample();

        UpdateCleanup();

        ScreenWriteout();
        if (frameleftToReset <= 0)
            frameleftToReset = TimerReset;
    }

    // SECTION: functions that make up Update() call (only called once from there)

    /// <summary>
    /// Does Setup for all versions of the collisions, Plain, Burst and Neon structures
    /// </summary>
    /// <param name="numChar">number of characters currently in game</param>
    /// <param name="characters">GameObjects of all characters</param>   
    private void DoSetup(int numChar, in GameObject[] characters)
    {
        int i = 0;
        foreach (GameObject gobj in characters)
        {
            Bounds playerBounds = gobj.GetComponent<Renderer>().bounds;
            float2 minB = new float2(playerBounds.min.x, playerBounds.min.z);
            float2 maxB = new float2(playerBounds.max.x, playerBounds.max.z);
            if (codeMode == Mode.Neon)
            {//Neon layout - unrolled version
                if (unrolled) // => true
                {
                    dynamicFloats[i] = maxB.x; dynamicFloats[numChar + i] = maxB.y;
                    dynamicFloats[2 * numChar + i] = -minB.x; dynamicFloats[3 * numChar + i] = -minB.y;
                }
                else // => false (second implementation)
                { //before unrolling, data layout was:
                    dynamicFloats[i * 4] = maxB.x; dynamicFloats[i * 4 + 1] = maxB.y;
                    dynamicFloats[i * 4 + 2] = -minB.x; dynamicFloats[i * 4 + 3] = -minB.y;
                }
            }
            else if (codeMode == Mode.Burst)
            {//burst layout
                dynamicFloats[i * 4] = maxB.x; dynamicFloats[i * 4 + 1] = maxB.y;
                dynamicFloats[i * 4 + 2] = minB.x; dynamicFloats[i * 4 + 3] = minB.y;//not negative or we just have to reverse them again for non-Neon implementation
            }
            dynamicObjectsVstatic[i] = new StaticCollisionObject { minPos = minB, maxPos = maxB };
            float2 pos = new float2(gobj.transform.position.x, gobj.transform.position.z);
            var collider = gobj.GetComponent<CapsuleCollider>();
            float rad = collider!=null ? collider.radius : DEFAULT_RADIUS;
            dynamicPositionsX[i] = gobj.transform.position.x;
            dynamicPositionsY[i] = gobj.transform.position.z;
            dynamicRadii[i] = rad;
            dynamicObjects[i] = new DynamicCollisionObject { position = pos, radius = rad };
            i += 1;
        }

        //different array type for burst jobs - not used by default
        if (useJobs) // => false (first implementation)
        {
            staticCollisionsBurst = new NativeArray<bool>(numWalls * numChar, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            dynamicCollisionsBurst = new NativeArray<bool>(numChar * numChar, Allocator.TempJob, NativeArrayOptions.ClearMemory);//clear memory is default anyway, but to be explicit
        }
    }

    private void DoWallsPlain(int numChar)
    {
        for (int c = 0; c < numChar; ++c)
        {
            //ideally would have numWalls a multiple of 4 and able to put w<(numWalls&~3) here so compiler can know not to generate tail code
            for (int s = 0; s < numWalls; ++s)
            {
                staticCollisions[c * numWalls + s] = dynamicObjectsVstatic[c].Intersects(staticObjects[s]);
            }
        }
    }

    private unsafe void DoWallsBurst(int numChar)
    {
        if (useJobs) // => false (first implementation)
        {
            var staticJob = new AABBObjCollisionDetectionJob
            {
                characters = dynamicObjectsVstatic,
                numChar = numChar,
                walls = staticObjects,
                collisions = staticCollisionsBurst,
            };
            staticJob.Schedule().Complete();
        }
        else // => true
        {
            fixed (bool* collisions = staticCollisions)
            fixed (float* wallFloats = staticFloats, charFloats = dynamicFloats)
            {
                AABBObjCollisionDetection(numChar, numWalls, wallFloats, charFloats, collisions);
            }
        }
    }

    private unsafe void DoWallsNeon(int numChar)
    {
        fixed (bool* collisions = staticCollisions)
        fixed (float* wallFloats = staticFloats, charFloats = dynamicFloats)
        {
            if (useJobs) // => false (first implementation)
            {
                var neonStaticJob = new NeonAABBObjCollisionDetectionJob
                {
                    numCharacters = numChar,
                    numWalls = numWalls,
                    collisions = collisions,
                    walls = wallFloats,
                    characters = charFloats
                };
                neonStaticJob.Schedule().Complete();
            }
            else // => true
            {
                if (unrolled) // => true (final implementation)
                {
                    NeonAABBObjCollisionDetectionUnrolled(numChar, numWalls, wallFloats, charFloats, collisions);
                }
                else // => false (second implementation)
                {
                    NeonAABBObjCollisionDetection(numChar, numWalls, wallFloats, charFloats, collisions);
                }
            }
        }
    }

    private void DoCharactersPlain(int numChar)
    {
        for (int c = 0; c < numChar; ++c)
        {
            for (int d = c + 1; d < numChar; ++d)
            {
                dynamicCollisions[c * numChar + d] = dynamicObjects[c].Intersects(dynamicObjects[d]);
            }
        }
    }

    private unsafe void DoCharactersBurst(int numChar)
    {
        if (useJobs) // => false (first implementation)
        {
            var dynamicJob = new RadiusObjCollisionDetectionJob
            {
                characters = dynamicObjects,
                numChar = numChar,
                collisions = dynamicCollisionsBurst,
            };
            dynamicJob.Schedule().Complete();
        }
        else // => true
        {
            fixed (bool* collisions = dynamicCollisions)
            fixed (float* xs = dynamicPositionsX, ys = dynamicPositionsY, rads = dynamicRadii)
            {
                RadiusObjCollisionDetection(numChar, xs, ys, rads, collisions);
            }
        }
    }

    private unsafe void DoCharactersNeon(int numChar)
    {
        fixed (bool* collisions = dynamicCollisions)
        fixed (float* xs = dynamicPositionsX, ys = dynamicPositionsY, rads = dynamicRadii)
        {
            if (useJobs) // => false (first implementation)
            {
                var neonDynamicJob = new NeonRadiusObjCollisionDetectionJob
                {
                    numChar = numChar,
                    collisions = collisions,
                    posXs = xs,
                    posYs = ys,
                    radii = rads
                };
                neonDynamicJob.Schedule().Complete();
            }
            else // => true
            {
                if (unrolled) // => true (final implementation)
                {
                    NeonRadiusObjCollisionDetectionUnrolled(numChar, xs, ys, rads, collisions);
                }
                else // => false (second implementation)
                {
                    NeonRadiusObjCollisionDetection(numChar, xs, ys, rads, collisions);
                }
            }
        }
    }

    private void CollisionMovement(int numChar, in GameObject[] characters)
    {
        for (int c = 0; c < numChar; ++c)
        {
            int numCharsLocal = 0;
            for (int d = c + 1; d < numChar; ++d)
            {
                if (dynamicCollisions[c * numChar + d])
                {
                    dynamicCollidingObjects[numCharsLocal] = dynamicObjects[d];
                    dObjectList[numCharsLocal] = characters[d];
                    numCharsLocal++;
                    if (numCharsLocal == 4)
                        break;
                }
            }
            if (numCharsLocal > 0)
            {//this moves both characters. call before movement due to walls, as walls should override if needed.
                RandomMovement.MoveClearFromCharacters(characters[c], dynamicObjects[c].radius, numCharsLocal, dynamicCollidingObjects, dObjectList);
            }
            int numWallsLocal = 0;
            for (int s = 0; s < numWalls; ++s)
            {
                if (staticCollisions[c * numWalls + s])
                {
                    staticCollidingObjects[numWallsLocal++] = staticObjects[s];
                    if (numWallsLocal == 2) break;
                }
            }
            if (numWallsLocal > 0)
            {
                characters[c].GetComponent<RandomMovement>().MoveClearFromWalls(numWallsLocal, staticCollidingObjects, dynamicObjects[c].radius);
            }
        }
    }

    private void UpdateCleanup()
    {
        dynamicObjects.Dispose();
        dynamicObjectsVstatic.Dispose();
        if (useJobs)
        {
            dynamicCollisionsBurst.Dispose();
            staticCollisionsBurst.Dispose();
        }
    }

    private void ScreenWriteout()
    {
        string modeString = (codeMode == Mode.Plain) ? "Standard mode" : "Bursted mode";
        string unrolledMode = "Unrolled Mode: False";
        if (codeMode == Mode.Neon)
        {
            modeString = UsingNeon() ? "Neon mode" : "NEON NOT SUPPORTED!!";
            unrolledMode = unrolled ? "Unrolled Mode: True" : "Unrolled Mode: False";
        }

        System.TimeSpan timeSinceStart = System.DateTime.UtcNow - startTime;
        UIText.text = modeString + "\n"
            + "Use Jobs :" + useJobs.ToString() + "\n"
            + unrolledMode + "\n"
            + string.Format(display, m_CurrentFps) + "\n"
            + "Time Running: " + timeSinceStart.Minutes.ToString() + ":" + timeSinceStart.Seconds.ToString("00") + "\n"
            + "Frame left to time correction: " + frameleftToReset.ToString() + "\n"
            + "Total Person Num: " + SpawningScript.numTotalPerson.ToString() + "\n"
            + "Setup Frame Time: " + setupFrameTime().ToString("N3") + "ms\n"
            + "CollideMove Frame Time: " + CollideMoveFrameTime().ToString("N3") + "ms\n"
            + "Wall Collisions: " + AABBCollideFrameTime().ToString("N3") + "ms\n"
            + "Character Collisions: " + radiusCollideFrameTime().ToString("N3") + "ms\n";
        //there are 2 other Timers available to write out - setupFrameTime and CollideMoveFrameTime - if you wish to see how they scale up as more characters enter scene
    }

    [BurstCompile]
    static bool UsingNeon()
    {
        return IsNeonSupported;
    }

    // SECTION:
    // Burst Radius-based collision detection functions

    /// <summary>
    /// Burst function for radius-based collision detection between characters
    /// </summary>
    /// <param name="numChar">number of characters currently in the scene</param>
    /// <param name="posXs">the x positions of the characters</param>
    /// <param name="posYs">the y positions of the characters</param>
    /// <param name="radii">the radii positions of the characters</param>
    /// <param name="collisions">an array of which characters are colliding</param>
    [BurstCompile]
    public static unsafe void RadiusObjCollisionDetection(int numChar, [NoAlias] in float* posXs, [NoAlias] in float* posYs, [NoAlias] in float* radii, [NoAlias] bool* collisions)
    {
        for (int c = 0; c < numChar; ++c)
        {
            for (int d = c + 1; d < numChar; ++d)
            {
                collisions[c * numChar + d] = RadiusIntersect(posXs[d]- posXs[c], posYs[d]-posYs[c], radii[c]+radii[d]);
            }
        }
    }
    /// <summary>
    /// Burst function for radius-based collision detection between characters
    /// </summary>
    /// <param name="numChar">number of characters currently in the scene</param>
    /// <param name="characters">array of charcter positions - max.x, max.y, min.x, min.y for each character in order</param>
    /// <param name="collisions">an array of which characters are colliding</param>
    [BurstCompile]
    public static unsafe void RadiusObjCollisionDetection(int numChar, [NoAlias] in float* characters, [NoAlias] bool* collisions)
    {
        for (int c = 0; c < numChar; ++c)
        {
            for (int d = c + 1; d < numChar; ++d)
            {
                collisions[c * numChar + d] = RadiusIntersect(characters[3 * d] - characters[3 * c], characters[3 * d + 1] - characters[3 * c + 1], characters[3 * c + 2] + characters[3 * d + 2]);
            }
        }
    }

    /// <summary>
    /// inline intersection calculation for Radius-based collisions
    /// </summary>
    /// <param name="xdiff">x distance between characters</param>
    /// <param name="ydiff">y distance between characters</param>
    /// <param name="allowedDistance">how close the charaacters can come before intersecting</param>
    /// <returns>whether there is a collision</returns>
    [BurstCompile]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool RadiusIntersect(float xdiff, float ydiff, float allowedDistance)
    {
        return (xdiff * xdiff + ydiff * ydiff) < (allowedDistance * allowedDistance);
    }

    /// <summary>
    /// Final working Neon Burst implementation of Radius-based collision detection
    /// </summary>
    /// <param name="numChar">number of characters currently in the scene</param>
    /// <param name="posXs">the x positions of the characters</param>
    /// <param name="posYs">the y positions of the characters</param>
    /// <param name="radii">the radii positions of the characters</param>
    /// <param name="collisions">an array of which characters are colliding</param>
    [BurstCompile]
    static unsafe void NeonRadiusObjCollisionDetectionUnrolled(int numChar, [NoAlias] in float* posXs, [NoAlias] in float* posYs, [NoAlias] in float* radii, [NoAlias] bool* collisions)
    {
        if (IsNeonSupported)
        {
            var tblindex1 = new Unity.Burst.Intrinsics.v64((byte)0, 4, 8, 12, 255, 255, 255, 255);//255=> out of range index will give 0
            var tblindex2 = new Unity.Burst.Intrinsics.v64((byte)255, 255, 255, 255, 0, 4, 8, 12);
            for (int i = 0; i < numChar; ++i)
            {
                var posX = vdupq_n_f32(posXs[i]);
                var posY = vdupq_n_f32(posYs[i]);
                var radius = vdupq_n_f32(radii[i]);
                int c = i+1;
                int numloops = (numChar - i)/8;
                for (int loop = 0; loop < numloops; ++loop)
                {
                    var charX = vld1q_f32(posXs + c);
                    var dx = vsubq_f32(posX, charX);
                    var dxSq = vmulq_f32(dx, dx);
                    var charY = vld1q_f32(posYs + c);
                    var dy = vsubq_f32(posY, charY);
                    var dySq = vmulq_f32(dy, dy);
                    var dsq = vaddq_f32(dxSq, dySq);

                    //although in our example all radii are the same, this copes if different characters have different radii
                    var r = vld1q_f32(radii + c);
                    var r_sum = vaddq_f32(radius, r);
                    var r_sum2 = vmulq_f32(r_sum, r_sum);
                    var mask = vqtbl1_u8(vcltq_f32(dsq, r_sum2), tblindex1);

                    c += 4;

                    charX = vld1q_f32(posXs + c);
                    dx = vsubq_f32(posX, charX);
                    dxSq = vmulq_f32(dx, dx);
                    charY = vld1q_f32(posYs + c);
                    dy = vsubq_f32(posY, charY);
                    dySq = vmulq_f32(dy, dy);
                    dsq = vaddq_f32(dxSq, dySq);

                    //although in our example all radii are the same, this copes if different characters have different radii
                    r = vld1q_f32(radii + c);
                    r_sum = vaddq_f32(radius, r);
                    r_sum2 = vmulq_f32(r_sum, r_sum);
                    mask = vqtbx1_u8(mask, vcltq_f32(dsq, r_sum2), tblindex2);

                    *(Unity.Burst.Intrinsics.v64*)(collisions + (i * numChar + c-4)) = mask;

                    c += 4;
                }
                if (c+3<numChar)
                {
                    var charX = vld1q_f32(posXs + c);
                    var dx = vsubq_f32(posX, charX);
                    var dxSq = vmulq_f32(dx, dx);
                    var charY = vld1q_f32(posYs + c);
                    var dy = vsubq_f32(posY, charY);
                    var dySq = vmulq_f32(dy, dy);
                    var dsq = vaddq_f32(dxSq, dySq);

                    //although in our example all radii are the same, this copes if different characters have different radii
                    var r = vld1q_f32(radii + c);
                    var r_sum = vaddq_f32(radius, r);
                    var r_sum2 = vmulq_f32(r_sum, r_sum);
                    var mask = vmovn_u32(vcltq_f32(dsq, r_sum2));//4x16bit answers after narrow
                    mask = vuzp1_u8(mask, mask);//4x8bit answers, then the same answers again after unzipping

                    *(uint*)(collisions + (i * numChar + c)) = vget_lane_u32(mask, 0);//put all 4 in at once

                    c += 4;
                }
                if (c < numChar)//if some left do last 1-3
                {
                    int startPoint = numChar - 4;
                    var charX = vld1q_f32(posXs + startPoint);
                    var dx = vsubq_f32(posX, charX);
                    var dxSq = vmulq_f32(dx, dx);
                    var charY = vld1q_f32(posYs + startPoint);
                    var dy = vsubq_f32(posY, charY);
                    var dySq = vmulq_f32(dy, dy);
                    var dsq = vaddq_f32(dxSq, dySq);

                    //although in our example all radii are the same, this copes if different characters have different radii
                    var r = vld1q_f32(radii + startPoint);
                    var r_sum = vaddq_f32(radius, r);
                    var r_sum2 = vmulq_f32(r_sum, r_sum);
                    var mask = vmovn_u32(vcltq_f32(dsq, r_sum2));//4x16bit answers
                    mask = vuzp1_u8(mask, mask);//4x8bit answers, then the same again

                    *(uint*)(collisions + (i * numChar + startPoint)) = vget_lane_u32(mask, 0);//put in 4 at once - overwrite some as needed (will be same result)

                }
            }
        }
        else
        {
            RadiusObjCollisionDetection(numChar, posXs, posYs, radii, collisions);
        }
    }

    // SECTION:
    // Burst AABB collision detection functions
    /// <summary>
    /// Burst AABB collision detection function
    /// </summary>
    /// <param name="numCharacters">number of characters active in the scene</param>
    /// <param name="numWalls">number of walls in the scene</param>
    /// <param name="walls">array of wall positions - min.x, min.y, max.x, max.y for each wall in order</param>
    /// <param name="characters">array of charcter positions - max.x, max.y, min.x, min.y for each character in order</param>
    /// <param name="collisions">returned bool array of which characters collide with which walls</param>
    [BurstCompile]
    public static unsafe void AABBObjCollisionDetection(int numCharacters, int numWalls, [NoAlias] in float* walls, [NoAlias] in float* characters, [NoAlias] bool* collisions)
    {
        for (int c = 0; c < numCharacters; ++c)
        {
            //ideally would have numWalls a multiple of 4 and able to put w<(numWalls&~3) here so compiler can know not to generate tail code
            for (int w = 0; w < numWalls; ++w)
            {
                //we have 2boxes; wall format [ min.x, min.y, max.x, max.y ] and char format [max.x, max.y, minx, min.y]
                collisions[c * numWalls + w] = AABBIntersect(walls[4 * w], walls[4 * w + 1], walls[4 * w + 2], walls[4 * w + 3],
                    characters[4 * c + 2], characters[4 * c + 3], characters[4 * c], characters[4 * c + 1]);
            }
        }
    }

    /// <summary>
    /// inline intersection calculation for AABB collisions
    /// </summary>
    /// <param name="wallMinX">minimum x position of the wall</param>
    /// <param name="wallMinY">minimum y position of the wall</param>
    /// <param name="wallMaxX">maximum x position of the wall</param>
    /// <param name="wallMaxY">maximum y position of the wall</param>
    /// <param name="charMinX">minimum x position of the character</param>
    /// <param name="charMinY">minimum y position of the character</param>
    /// <param name="charMaxX">maximum x position of the character</param>
    /// <param name="charMaxY">maximum y position of the character</param>
    /// <returns>whether the wall and character are colliding</returns>
    [BurstCompile]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool AABBIntersect(float wallMinX, float wallMinY, float wallMaxX, float wallMaxY, float charMinX, float charMinY, float charMaxX, float charMaxY)
    {
        return !(charMinX > wallMaxX
              || charMaxX < wallMinX
              || charMinY > wallMaxY
              || charMaxY < wallMinY);
    }

    /// <summary>
    /// Final working Neon Burst implementation of AABB collision detection
    /// </summary>
    /// <param name="numCharacters">number of characters active in the scene</param>
    /// <param name="numWalls">number of walls in the scene</param>
    /// <param name="walls">array of wall positions - all walls min.x, then all min.y, then all -max.x, then all -max.y</param>
    /// <param name="characters">array of charcter positions - all characters max.x, then all max.y, then all -min.x, then all -min.y</param>
    /// <param name="collisions">returned bool array of which characters collide with which walls</param>
    [BurstCompile]
    static unsafe void NeonAABBObjCollisionDetectionUnrolled(int numCharacters, int numWalls, [NoAlias] in float* walls, [NoAlias] in float* characters, [NoAlias] bool* collisions)
    {
        if (IsNeonSupported)
        {
            var tblindex1 = new Unity.Burst.Intrinsics.v64((byte)0, 4, 8, 12, 255, 255, 255, 255);//255=> out of range index will give 0
            var tblindex2 = new Unity.Burst.Intrinsics.v64((byte)255, 255, 255, 255, 0, 4, 8, 12);

            //let's try unrolling!  Lay out our data differently -> 
            int c = 0;
            for (; c < numCharacters; ++c)
            {
                var charMaxXs = vdupq_n_f32(*(characters + c));
                var charMaxYs = vdupq_n_f32(*(characters + numCharacters + c));
                var charMinusMinXs = vdupq_n_f32(*(characters + 2*numCharacters + c));
                var charMinusMinYs = vdupq_n_f32(*(characters + 3*numCharacters + c));

                int w = 0;
                for (; w < (numWalls&~7); w+=4)
                {
                    var wallMinXs = vld1q_f32(walls + w);
                    var wallMinYs = vld1q_f32(walls + numWalls + w);
                    var wallMinusMaxXs = vld1q_f32(walls + 2*numWalls + w);
                    var wallMinusMaxYs = vld1q_f32(walls + 3*numWalls + w);

                    var results = vqtbl1_u8(vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)), 
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs))), tblindex1);

                    w += 4;

                    wallMinXs = vld1q_f32(walls + w);
                    wallMinYs = vld1q_f32(walls + numWalls + w);
                    wallMinusMaxXs = vld1q_f32(walls + 2 * numWalls + w);
                    wallMinusMaxYs = vld1q_f32(walls + 3 * numWalls + w);

                    results = vmvn_u8(vqtbx1_u8(results, vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs))), tblindex2));

                    //straight into array
                    *(Unity.Burst.Intrinsics.v64*)(collisions + (c * numWalls + w - 4)) = results;
                }
                if (w+3<numWalls)
                {
                    var wallMinXs = vld1q_f32(walls + w);
                    var wallMinYs = vld1q_f32(walls + numWalls + w);
                    var wallMinusMaxXs = vld1q_f32(walls + 2 * numWalls + w);
                    var wallMinusMaxYs = vld1q_f32(walls + 3 * numWalls + w);

                    var results = vmovn_u32(vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs))));//4x16bit answers after narrow
                    results = vmvn_u8(vuzp1_u8(results, results));//4x8bit answers, then the same answers again after unzipping; then negated

                    *(uint*)(collisions + c * numWalls + w) = vget_lane_u32(results, 0);//put all 4 in at once
                }
                //if we guarantee numWalls to be a multiple of 4, we won't need this tail code.
                /*if (w < numWalls)
                {
                    var wallMinXs = vld1q_f32(walls + w);
                    var wallMinYs = vld1q_f32(walls + numWalls + w);
                    var wallMinusMaxXs = vld1q_f32(walls + 2 * numWalls + w);
                    var wallMinusMaxYs = vld1q_f32(walls + 3 * numWalls + w);

                    var results = vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs)));

                    collisions[c * numWalls + w] = vgetq_lane_u32(results, 0) == 0;
                    if ((w+1) < numWalls) collisions[c * numWalls + w+1] = vgetq_lane_u32(results, 1) == 0;
                    if ((w+2) < numWalls) collisions[c * numWalls + w+2] = vgetq_lane_u32(results, 2) == 0;
                }*/
            }
        }
        else
        {
            // Data in wrong format for anything else!!
            // Real game would require fallback
        }
    }

    //===============================================================================================================================================
    // Unused versions of functions - second implementation before unrolling
    //UNUSED - superseded by Unrolled version
    /// <summary>
    /// Second attempt of Neon Burst implementation of Radius-based collision detection
    /// </summary>
    /// <param name="numChar">number of characters currently in the scene</param>
    /// <param name="posXs">the x positions of the characters</param>
    /// <param name="posYs">the y positions of the characters</param>
    /// <param name="radii">the radii positions of the characters</param>
    /// <param name="collisions">an array of which characters are colliding</param>
    [BurstCompile]
    static unsafe void NeonRadiusObjCollisionDetection(int numChar, [NoAlias] in float* posXs, [NoAlias] in float* posYs, [NoAlias] in float* radii, [NoAlias] bool* collisions)
    {
        if (IsNeonSupported)
        {
            for (int i = 0; i < numChar; ++i)
            {
                var posX = vdupq_n_f32(posXs[i]);
                var posY = vdupq_n_f32(posYs[i]);
                var radius = vdupq_n_f32(radii[i]);
                int c = i + 1;
                for (; c < (numChar - (i % 4)); c += 4)
                {
                    var charX = vld1q_f32(posXs + c);
                    var dx = vsubq_f32(posX, charX);
                    var dxSq = vmulq_f32(dx, dx);
                    var charY = vld1q_f32(posYs + c);
                    var dy = vsubq_f32(posY, charY);
                    var dySq = vmulq_f32(dy, dy);
                    var dsq = vaddq_f32(dxSq, dySq);

                    //although in our example all radii are the same, this copes if different characters have different radii
                    var r = vld1q_f32(radii + c);
                    var r_sum = vaddq_f32(radius, r);
                    var r_sum2 = vmulq_f32(r_sum, r_sum);
                    var mask = vcltq_f32(dsq, r_sum2);

                    //if (vaddvq_u32(mask) == 0) continue; // having a break branch slowed the code down
                    collisions[i * numChar + c] = vgetq_lane_u32(mask, 0) > 0;
                    collisions[i * numChar + c + 1] = vgetq_lane_u32(mask, 1) > 0;
                    collisions[i * numChar + c + 2] = vgetq_lane_u32(mask, 2) > 0;
                    collisions[i * numChar + c + 3] = vgetq_lane_u32(mask, 3) > 0;
                }
                if (c < numChar)//if some left do last 1-3
                {
                    float2 dco2 = new float2(posXs[i], posYs[i]);
                    var i_pos = vld1_f32(&dco2.x);
                    do
                    {
                        var dco1 = new float2(posXs[c], posYs[c]);
                        var c_pos = vld1_f32(&dco1.x);
                        var dist = vsub_f32(c_pos, i_pos);
                        var dist2 = vmul_f32(dist, dist);
                        float d2 = vpadds_f32(dist2);
                        float r_sum = radii[c] + radii[i];
                        float r_sum2 = r_sum * r_sum;
                        collisions[i * numChar + c] = d2 < r_sum2;

                        ++c;
                    } while (c < numChar);
                }
            }
        }
        else
        {
            RadiusObjCollisionDetection(numChar, posXs, posYs, radii, collisions);
        }
    }

    //UNUSED - superseded by Unrolled version
    /// <summary>
    /// second attempt at Neon Burst implementation of AABB collision detection
    /// </summary>
    /// <param name="numCharacters">number of characters active in the scene</param>
    /// <param name="numWalls">number of walls in the scene</param>
    /// <param name="walls">array of wall positions - min.x, min.y, -max.x, -max.y for all walls in order</param>
    /// <param name="characters">array of charcter positions - max.x, max.y, -min.x, -min.y for all characters in order</param>
    /// <param name="collisions">returned bool array of which characters collide with which walls</param>
    [BurstCompile]
    static unsafe void NeonAABBObjCollisionDetection(int numCharacters, int numWalls, [NoAlias] in float* walls, [NoAlias] in float* characters, [NoAlias] bool* collisions)
    {
        if (IsNeonSupported)
        {
            for (int c = 0; c < numCharacters; ++c)
            {
                var charLimits = vld1q_f32(characters + (4 * c));

                for (int w = 0; w < numWalls; ++w)
                {
                    var wallLimits = vld1q_f32(walls + (4 * w));
                    //we have 2boxes; format [ min.x, min.y, -max.x, -max.y ] and [max.x, max.y, -minx, -min.y]
                    collisions[c * numWalls + w] = vmaxvq_u32(vcgeq_f32(wallLimits, charLimits)) == 0;
                }
            }
        }
        else
        {
            // Data in wrong format for anything else!!
            // Real game would require fallback
        }
    }

    //====================================================================================================================================================
    //attempt at further unrolling that timing revealed was not worth it
    //THIS IS UNUSED - extra code gains nothing in speed over first Unrolled version
    [BurstCompile]
    static unsafe void NeonAABBObjCollisionDetectionUnrolled2(int numCharacters, int numWalls, [NoAlias] in float* walls, [NoAlias] in float* characters, [NoAlias] bool* collisions)
    {
        if (IsNeonSupported)
        {
            //let's try unrolling further!  going to need significant amount of data to justify => most needs to be processed in main loops, not tail-code
            // (ideally avoid tail code => can we guarantee that there is a multiple of 4 number of walls?)
            // no advantage / ever-so-slightly slower?  
            // What we gain on the staying in vectors we lose on the lack of data coherence on the stores?
            int c = 0;
            for (; c < (numCharacters & ~3); c += 4)
            {
                var charMaxXs = vld1q_f32(characters + c);
                var charMaxYs = vld1q_f32(characters + numCharacters + c);
                var charMinusMinXs = vld1q_f32(characters + 2 * numCharacters + c);
                var charMinusMinYs = vld1q_f32(characters + 3 * numCharacters + c);

                int w = 0;
                for (; w < (numWalls & ~3); w += 4)
                {
                    var wallMinXs = vld1q_f32(walls + w);
                    var wallMinYs = vld1q_f32(walls + numWalls + w);
                    var wallMinusMaxXs = vld1q_f32(walls + 2 * numWalls + w);
                    var wallMinusMaxYs = vld1q_f32(walls + 3 * numWalls + w);

                    var results = vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs)));

                    collisions[c * numWalls + w] = vgetq_lane_u32(results, 0) == 0;
                    collisions[c + 1 * numWalls + w + 1] = vgetq_lane_u32(results, 1) == 0;
                    collisions[c + 2 * numWalls + w + 2] = vgetq_lane_u32(results, 2) == 0;
                    collisions[c + 3 * numWalls + w + 3] = vgetq_lane_u32(results, 3) == 0;

                    //1234 => 2143
                    wallMinXs = vrev64q_f32(wallMinXs);
                    wallMinYs = vrev64q_f32(wallMinYs);
                    wallMinusMaxXs = vrev64q_f32(wallMinusMaxXs);
                    wallMinusMaxYs = vrev64q_f32(wallMinusMaxYs);

                    results = vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs)));

                    collisions[c * numWalls + w+1] = vgetq_lane_u32(results, 0) == 0;
                    collisions[c + 1 * numWalls + w] = vgetq_lane_u32(results, 1) == 0;
                    collisions[c + 2 * numWalls + w + 3] = vgetq_lane_u32(results, 2) == 0;
                    collisions[c + 3 * numWalls + w + 2] = vgetq_lane_u32(results, 3) == 0;

                    //2143 => 4321
                    wallMinXs = vextq_f32(wallMinXs, wallMinXs, 2);
                    wallMinYs = vextq_f32(wallMinYs, wallMinYs, 2);
                    wallMinusMaxXs = vextq_f32(wallMinusMaxXs, wallMinusMaxXs, 2);
                    wallMinusMaxYs = vextq_f32(wallMinusMaxYs, wallMinusMaxYs, 2);

                    results = vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs)));

                    collisions[c * numWalls + w + 3] = vgetq_lane_u32(results, 0) == 0;
                    collisions[c + 1 * numWalls + w+2] = vgetq_lane_u32(results, 1) == 0;
                    collisions[c + 2 * numWalls + w+1] = vgetq_lane_u32(results, 2) == 0;
                    collisions[c + 3 * numWalls + w] = vgetq_lane_u32(results, 3) == 0;

                    //4321 => 3412
                    wallMinXs = vrev64q_f32(wallMinXs);
                    wallMinYs = vrev64q_f32(wallMinYs);
                    wallMinusMaxXs = vrev64q_f32(wallMinusMaxXs);
                    wallMinusMaxYs = vrev64q_f32(wallMinusMaxYs);

                    results = vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs)));

                    collisions[c * numWalls + w + 2] = vgetq_lane_u32(results, 0) == 0;
                    collisions[c + 1 * numWalls + w+3] = vgetq_lane_u32(results, 1) == 0;
                    collisions[c + 2 * numWalls + w] = vgetq_lane_u32(results, 2) == 0;
                    collisions[c + 3 * numWalls + w+1] = vgetq_lane_u32(results, 3) == 0;

                }
                //if we guaranteed numWalls to be a multiple of 4, we wouldn't need this tail code.
                while (w < numWalls)
                {
                    var wallMinXs = vdupq_n_f32(*(walls + w));
                    var wallMinYs = vdupq_n_f32(*(walls + numWalls + w));
                    var wallMinusMaxXs = vdupq_n_f32(*(walls + 2 * numWalls + w));
                    var wallMinusMaxYs = vdupq_n_f32(*(walls + 3 * numWalls + w));

                    var results = vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs)));

                    collisions[c * numWalls + w] = vgetq_lane_u32(results, 0) == 0;
                    collisions[c+1 * numWalls + w] = vgetq_lane_u32(results, 1) == 0;
                    collisions[c+2 * numWalls + w] = vgetq_lane_u32(results, 2) == 0;
                    collisions[c+3 * numWalls + w] = vgetq_lane_u32(results, 3) == 0;
                }
            }
            while (c < numCharacters)
            {
                var charMaxXs = vdupq_n_f32(*(characters + c));
                var charMaxYs = vdupq_n_f32(*(characters + numCharacters + c));
                var charMinusMinXs = vdupq_n_f32(*(characters + 2 * numCharacters + c));
                var charMinusMinYs = vdupq_n_f32(*(characters + 3 * numCharacters + c));

                int w = 0;
                for (; w < (numWalls & ~3); w += 4)
                {
                    var wallMinXs = vld1q_f32(walls + w);
                    var wallMinYs = vld1q_f32(walls + numWalls + w);
                    var wallMinusMaxXs = vld1q_f32(walls + 2 * numWalls + w);
                    var wallMinusMaxYs = vld1q_f32(walls + 3 * numWalls + w);

                    var results = vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs)));

                    collisions[c * numWalls + w] = vgetq_lane_u32(results, 0) == 0;
                    collisions[c * numWalls + w + 1] = vgetq_lane_u32(results, 1) == 0;
                    collisions[c * numWalls + w + 2] = vgetq_lane_u32(results, 2) == 0;
                    collisions[c * numWalls + w + 3] = vgetq_lane_u32(results, 3) == 0;
                }
                //if we guaranteed numWalls to be a multiple of 4, we wouldn't need this tail code.
                if (w < numWalls)
                {
                    var wallMinXs = vld1q_f32(walls + w);
                    var wallMinYs = vld1q_f32(walls + numWalls + w);
                    var wallMinusMaxXs = vld1q_f32(walls + 2 * numWalls + w);
                    var wallMinusMaxYs = vld1q_f32(walls + 3 * numWalls + w);

                    var results = vorrq_u32(vorrq_u32(vcgeq_f32(wallMinXs, charMaxXs), vcgeq_f32(wallMinYs, charMaxYs)),
                                            vorrq_u32(vcgeq_f32(wallMinusMaxXs, charMinusMinXs), vcgeq_f32(wallMinusMaxYs, charMinusMinYs)));

                    collisions[c * numWalls + w] = vgetq_lane_u32(results, 0) == 0;
                    if ((w + 1) < numWalls) collisions[c * numWalls + w + 1] = vgetq_lane_u32(results, 1) == 0;
                    if ((w + 2) < numWalls) collisions[c * numWalls + w + 2] = vgetq_lane_u32(results, 2) == 0;
                }
                ++c;
            }
        }
        else
        {
            // Data in wrong format for anything else!!
            // Real game would require fallback
        }
    }
    //================================================================================================================================================
    // end of unused Burst static functions
}


//================================================================================================================================================
//================================================================================================================================================
//UNUSED - Jobs below are replaced by static Burst functions above
// to re-enable change the const "useJobs"
[BurstCompile]
public unsafe struct AABBObjCollisionDetectionJob : IJob
{
    [WriteOnly]
    public NativeArray<bool> collisions;

    //[ReadOnly]
    public NativeArray<StaticCollisionObject> characters;
    [ReadOnly]
    public int numChar;

    //[ReadOnly]
    public NativeArray<StaticCollisionObject> walls;

    public void Execute()
    {
        var wallsPtr = (float *)walls.GetUnsafePtr<StaticCollisionObject>();
        var charactersPtr = (float *)characters.GetUnsafePtr<StaticCollisionObject>();
        CollisionCalculationScript.AABBObjCollisionDetection(numChar, walls.Length, in wallsPtr, in charactersPtr, (bool*)collisions.GetUnsafePtr<bool>());
        /*for (int c = 0; c < numChar; ++c)
        {
            for (int s = 0; s < walls.Length; ++s)
            {
               collisions[c * walls.Length + s] = characters[c].Intersects(walls[s]);
            }
        }*/
    }
}

[BurstCompile]
public unsafe struct NeonAABBObjCollisionDetectionJob : IJob
{
    [WriteOnly]
    public bool* collisions;

    [ReadOnly]
    public int numCharacters;

    [ReadOnly]
    public int numWalls;

    [NativeDisableUnsafePtrRestriction]
    [ReadOnly]
    public float* walls;
    [NativeDisableUnsafePtrRestriction]
    [ReadOnly]
    public float* characters;

    public void Execute()
    {
        if (IsNeonSupported)
        {
            for (int c = 0; c < numCharacters; ++c)
            {
                var charLimits = vld1q_f32(characters + (4 * c));
                //did below thru data grooming
                /*                //flip max & min over for comparison
                                charLimits = vcombine_f32(vget_high_f32(charLimits), vget_low_f32(charLimits));
                                //flip signs so they're all less than
                                charLimits = vmulq_n_f32(charLimits, -1f);*/

                for (int w = 0; w < numWalls; ++w)
                {
                    var wallLimits = vld1q_f32(walls + (4 * w));
                    //we have 2boxes; format [ min.x, min.y, -max.x, -max.y ] and [max.x, max.y, -minx, -min.y]
                    collisions[c * numWalls + w] = vmaxvq_u32(vcgeq_f32(wallLimits, charLimits)) == 0;
                }
            }
        }
    }
}

//radius-based jobs (unused)
[BurstCompile]
public unsafe struct RadiusObjCollisionDetectionJob : IJob
{
    [WriteOnly]
    public NativeArray<bool> collisions;

    //[ReadOnly]
    public NativeArray<DynamicCollisionObject> characters;
    [ReadOnly]
    public int numChar;

    public void Execute()
    {
        var charactersPtr = (float*)characters.GetUnsafePtr<DynamicCollisionObject>();
        CollisionCalculationScript.RadiusObjCollisionDetection(numChar, in charactersPtr, (bool*)collisions.GetUnsafePtr<bool>());
        /*for (int c = 0; c < numChar; ++c)
        {
            for (int d = c + 1; d < numChar; ++d)
            {
                collisions[c * numChar + d] = characters[c].Intersects(characters[d]);
            }
        }*/
    }
}

[BurstCompile]
public unsafe struct NeonRadiusObjCollisionDetectionJob : IJob
{
    [WriteOnly]
    public bool* collisions;

    [ReadOnly]
    public int numChar;

    [NativeDisableUnsafePtrRestriction]
    [ReadOnly]
    public float* posXs;
    [NativeDisableUnsafePtrRestriction]
    [ReadOnly]
    public float* posYs;
    [NativeDisableUnsafePtrRestriction]
    [ReadOnly]
    public float* radii;

    public void Execute()
    {
        if (IsNeonSupported)
        {

            for (int i = 0; i < numChar; ++i)
            {
                var posX = vdupq_n_f32(posXs[i]);
                var posY = vdupq_n_f32(posYs[i]);
                var radius = vdupq_n_f32(radii[i]);
                int c = i + 1;
                for (; c < (numChar - (i % 4)); c += 4)
                {
                    var charX = vld1q_f32(posXs + c);
                    var dx = vsubq_f32(posX, charX);
                    var dxSq = vmulq_f32(dx, dx);
                    var charY = vld1q_f32(posYs + c);
                    var dy = vsubq_f32(posY, charY);
                    var dySq = vmulq_f32(dy, dy);
                    var dsq = vaddq_f32(dxSq, dySq);

                    var r = vld1q_f32(radii + c);
                    var r_sum = vaddq_f32(radius, r);
                    var r_sum2 = vmulq_f32(r_sum, r_sum);
                    var mask = vcltq_f32(dsq, r_sum2);

                    //if (vaddvq_u32(mask) == 0) continue; // having a break branch slowed the code down
                    collisions[i * numChar + c] = vgetq_lane_u32(mask, 0) > 0;
                    collisions[i * numChar + c + 1] = vgetq_lane_u32(mask, 1) > 0;
                    collisions[i * numChar + c + 2] = vgetq_lane_u32(mask, 2) > 0;
                    collisions[i * numChar + c + 3] = vgetq_lane_u32(mask, 3) > 0;
                    // significant slow down from writing to mirrored data -> changed code that checked collisions so this wasn't needed.
                    /*collisions[c * numChar + i] = collisions[i * numChar + c];
                    collisions[(c + 1) * numChar + i] = collisions[i * numChar + c + 1];
                    collisions[(c + 2) * numChar + i] = collisions[i * numChar + c + 2];
                    collisions[(c + 3) * numChar + i] = collisions[i * numChar + c + 3];//*/
                }
                if (c < numChar)//if some left do last 1-3
                {
                    float2 dco2 = new float2(posXs[i], posYs[i]);
                    var i_pos = vld1_f32(&dco2.x);
                    do
                    {
                        var dco1 = new float2(posXs[c], posYs[c]);
                        var c_pos = vld1_f32(&dco1.x);
                        var dist = vsub_f32(c_pos, i_pos);
                        var dist2 = vmul_f32(dist, dist);
                        float d2 = vpadds_f32(dist2);
                        float r_sum = radii[c] + radii[i];
                        float r_sum2 = r_sum * r_sum;
                        collisions[i * numChar + c]  = d2 < r_sum2;
                        //collisions[c * numChar + i] = collisions[i * numChar + c];//significant slowdown so changed data use as above

                        ++c;
                    } while (c < numChar);
                }
            }
        }
    }
}

// end Jobs implementation (now unused)
//===========================================================================================================================
