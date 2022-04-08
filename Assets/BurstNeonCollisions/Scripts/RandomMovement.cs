/*
 * This confidential and proprietary software may be used only as
 * authorised by a licensing agreement from ARM Limited
 * (C) COPYRIGHT 2021 ARM Limited
 * ALL RIGHTS RESERVED
 * The entire notice above must be reproduced on all authorised
 * copies and copies may only be made to the extent permitted
 * by a licensing agreement from ARM Limited.
 */

using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;

//script to generate random movement of characters, and to resolve any collisions
public class RandomMovement : MonoBehaviour
{
    [Range(1f, 10f)]
    public float TimeToDirectionChange = 5f;
    [Range(0.1f, 20f)]
    public float speed = 3.5f;

    private float directionChangeCounter;
    public Vector2 direction { get; private set; }
    const float bounds = 50f;

    // Start is called before the first frame update
    void Start()
    {
        directionChangeCounter = TimeToDirectionChange;
        generateDirection();
    }

    private void generateDirection()
    {
        Vector2 position = new Vector2(gameObject.transform.position.x, gameObject.transform.position.z);
        direction = UnityEngine.Random.insideUnitCircle;
        Vector2 aimPoint = position +  direction * speed * TimeToDirectionChange;
        if (aimPoint.x > bounds || aimPoint.x < -bounds) direction.Set(-direction.x, direction.y);
        if (aimPoint.y > bounds || aimPoint.y < -bounds) direction.Set(direction.x, -direction.y);
    }

    // Update is called once per frame
    void Update()
    {
        directionChangeCounter -= Time.deltaTime;
        if (directionChangeCounter < 0)
        {
            generateDirection();
            directionChangeCounter = TimeToDirectionChange;
        }

        Vector2 movement = direction * Time.deltaTime;
        gameObject.transform.Translate(new Vector3(movement.x, 0f, movement.y));
    }


    /// <summary>
    /// This function keeps the characters clear of other characters. It has not been optimized much
    /// </summary>
    /// <param name="dynamicObjects">Positions and radii of other characters that are colliding with this one</param>
    /// <param name="dynamicGameObjs">Actual game objects of colliding other characters so we can move them as well</param>
    public static void MoveClearFromCharacters(in GameObject thisGameObject, float ourRadius, int numChars, in NativeArray<DynamicCollisionObject> dynamicObjects, in GameObject[] dynamicGameObjs)
    {
        float2 ourPosition = new float2(thisGameObject.transform.position.x, thisGameObject.transform.position.z);

        for (int d = 0; d < numChars; ++d)
        {
            float reqDistance = ourRadius + dynamicObjects[d].radius;
            float2 directionBetween = ourPosition - dynamicObjects[d].position;
            float distance = math.length(directionBetween);
            float adjust = (reqDistance - distance) / 2f;
            if (adjust <= 0f) continue;
            directionBetween = (distance == 0f) ? new float2(UnityEngine.Random.insideUnitCircle) : math.normalize(directionBetween);//cover rare case where they are in exactly the same position even though 2xfloat
            thisGameObject.transform.Translate(new Vector3(directionBetween.x * adjust, 0f, directionBetween.y * adjust));
            dynamicGameObjs[d].transform.Translate(new Vector3(directionBetween.x * -adjust, 0f, directionBetween.y * -adjust));
        }
    }


    /// <summary>
    /// This function keeps the characters clear of walls. It has not been optimized much
    /// call after move clear from characters as this one is more important
    /// </summary>
    /// <param name="staticObjects">Wall objects that are colliding with this character, so we have their positions</param>
    public void MoveClearFromWalls(int numWalls, in NativeArray<StaticCollisionObject> staticObjects, float ourRadius)
    {
        float2 ourPosition = new float2(gameObject.transform.position.x, gameObject.transform.position.z);

        for (int s = 0; s < numWalls; ++s)
        {
            Vector3 newPosition = gameObject.transform.position;
            Quaternion rotation = gameObject.transform.rotation;
            if ((staticObjects[s].minPos.x > ourPosition.x) ^ (staticObjects[s].maxPos.x < ourPosition.x))
            {//x overlap
                if ((staticObjects[s].minPos.y > ourPosition.y) ^ (staticObjects[s].maxPos.y < ourPosition.y))
                {//z overlap as well!  Don't suddenly jump to wrong corner
                    bool lowside = direction.y > 0;
                    if (ourPosition.y > staticObjects[s].maxPos.y)
                        lowside = false;
                    else if (ourPosition.y < staticObjects[s].minPos.y)
                        lowside = true;
                    if (lowside)
                    {
                        if (staticObjects[s].minPos.y - ourPosition.y < ourRadius)
                        {//don't jump somewhere else
                            newPosition.z = staticObjects[s].minPos.y - ourRadius;
                            newPosition.z = math.min(gameObject.transform.position.z, newPosition.z);//in case of 2 collisions, don't have second over-rule.
                        }
                    }
                    else
                    {
                        if (staticObjects[s].maxPos.y - ourPosition.y < ourRadius)
                        {//don't jump somewhere else
                            newPosition.z = staticObjects[s].maxPos.y + ourRadius;
                            newPosition.z = math.max(gameObject.transform.position.z, newPosition.z);//in case of 2 collisions, don't have second over-rule.
                        }
                    }
                    lowside = direction.x > 0;
                    if (ourPosition.x > staticObjects[s].maxPos.x)
                        lowside = false;
                    else if (ourPosition.x < staticObjects[s].minPos.x)
                        lowside = true;
                    if (lowside)
                    {
                        if (staticObjects[s].minPos.x - ourPosition.x < ourRadius)
                        {//don't jump somewhere else
                            newPosition.x = staticObjects[s].minPos.x - ourRadius;
                            newPosition.x = math.min(gameObject.transform.position.x, newPosition.x);//in case of 2 collisions, don't have second over-rule.
                        }
                    }
                    else
                    {
                        if (staticObjects[s].maxPos.x - ourPosition.x < ourRadius)
                        {//don't jump somewhere else
                            newPosition.x = staticObjects[s].maxPos.x + ourRadius;
                            newPosition.x = math.max(gameObject.transform.position.x, newPosition.x);//in case of 2 collisions, don't have second over-rule.
                        }
                    }
                }
                else
                { //just x overlap
                    bool lowside = direction.x > 0;
                    if (ourPosition.x > staticObjects[s].maxPos.x)
                        lowside = false;
                    else if (ourPosition.x < staticObjects[s].minPos.x)
                        lowside = true;
                    if (lowside)
                    {
                        newPosition.x = staticObjects[s].minPos.x - ourRadius;
                        newPosition.x = math.min(gameObject.transform.position.x, newPosition.x);//in case of 2 collisions, don't have second over-rule.
                    }
                    else
                    {
                        newPosition.x = staticObjects[s].maxPos.x + ourRadius;
                        newPosition.x = math.max(gameObject.transform.position.x, newPosition.x);//in case of 2 collisions, don't have second over-rule.
                    }
                }
            }
            else if ((staticObjects[s].minPos.y > ourPosition.y) ^ (staticObjects[s].maxPos.y < ourPosition.y))
            {//z overlap
                bool lowside = direction.y > 0;
                if (ourPosition.y > staticObjects[s].maxPos.y)
                    lowside = false;
                else if (ourPosition.y < staticObjects[s].minPos.y)
                    lowside = true;
                if (lowside)
                {
                    newPosition.z = staticObjects[s].minPos.y-ourRadius;
                    newPosition.z = math.min(gameObject.transform.position.z, newPosition.z);//in case of 2 collisions, don't have second over-rule.
                }
                else
                {
                    newPosition.z = staticObjects[s].maxPos.y+ourRadius;
                    newPosition.z = math.max(gameObject.transform.position.z, newPosition.z);//in case of 2 collisions, don't have second over-rule.
                }
            }
            gameObject.transform.SetPositionAndRotation(newPosition, rotation);
        }
    }
}
