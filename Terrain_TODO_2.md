## Terrain todo:
I want to remove the disconnect between distant terrain and nearby terrain by making the transition smoother between low quality distant renders and high quality nearby renders. One idea on how to do that is the follows:

1. subdivide the nodes in 3 distinct distances- short: complete node rendering, medium- 4 node clumps, long- 16 node clumps. Each clump displays as larger versions of their predominant type. This makes far away terrain appear as large raw nodes with a small topsoil top.
2. extend short distance renders and medium distance renders to be a larger area. that way the transition is further away and less noticable. 
3. As the more detailed chunks load in, the less detailed chunks become more transparent until they are no longer visible. The same is true when leaving/entering an area, the idea is that the handoff between more detailed and less detailed or vice versa is done via a fade in/out transition. 

## Planet scaling issue:
The planet still feels like a curved ball. Please extend the diameter of the planet by 100%.

## Sky islands
Sky islands are looking good in terms of shape and vibe, but I want them to be able to be MUCH larger with some central islands and other smaller islands cracking off of them.