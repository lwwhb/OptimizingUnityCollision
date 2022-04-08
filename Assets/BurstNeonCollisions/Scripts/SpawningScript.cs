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

//simple spawning script to rapidly create a lot of characters for colliding
public class SpawningScript : MonoBehaviour
{
    public GameObject myPrefab;
    [Range(10, 600)]//if adjust max number here, adjust CollisionCalculationScript maxCharacters
    public int numToSpawn = 30;
    [Range(0.2f, 5)]
    public float timeBetweenSpawns = 2.5f;
    private float timeToSpawn;
    public Vector3 centralSpawnPoint = new Vector3(0, 0, 0);
    [Range(5,65)]
    public float spawnSpread = 57;

    public static int numTotalPerson = 0;

    private Vector3 spawn2, spawn3, spawn4, spawn5;

    // Start is called before the first frame update
    void Start()
    {
        timeToSpawn = timeBetweenSpawns;
        spawn2 = new Vector3(centralSpawnPoint.x + spawnSpread, 0, centralSpawnPoint.z + spawnSpread);
        spawn3 = new Vector3(centralSpawnPoint.x - spawnSpread, 0, centralSpawnPoint.z - spawnSpread);
        spawn4 = new Vector3(centralSpawnPoint.x + spawnSpread, 0, centralSpawnPoint.z - spawnSpread);
        spawn5 = new Vector3(centralSpawnPoint.x - spawnSpread, 0, centralSpawnPoint.z + spawnSpread);
    }

    // Update is called once per frame
    void Update()
    {
        timeToSpawn -= Time.deltaTime;
        if (timeToSpawn < 0 && numToSpawn>0)
        {//spawn 5 at different points each time to increase number quickly
            Instantiate(myPrefab, centralSpawnPoint, Quaternion.identity);
            Instantiate(myPrefab, spawn2, Quaternion.identity);
            Instantiate(myPrefab, spawn3, Quaternion.identity);
            Instantiate(myPrefab, spawn4, Quaternion.identity);
            Instantiate(myPrefab, spawn5, Quaternion.identity);
            timeToSpawn = timeBetweenSpawns;
            numToSpawn -= 5;
            numTotalPerson += 5;
        }
    }
}
