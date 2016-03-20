This tool generates POV-ray code for an extruded object with rounded edges.

BEFORE
=

![](http://i.imgur.com/Y99o4oG.png)

AFTER
=

![](http://i.imgur.com/KADz9ra.png)

As seen above, typical extruded objects have unrealistically sharp edges.

This tool generates a series of Bézier patches that give the object a rounded edge.
Note that this makes the object appear fatter; you can counteract this by making
the original input object slimmer.

Command line parameters used to generate the above example were:

* `-s 6` (maximum smoothness)
* `-f .55` (rounding factor close to circular)
* `-d 20` (depth of extrusion)
* `-r 2` (rounding radius; also the amount by which the object gets fatter)

The tool can generate objects from:

* text and a font;
* a curve defined in an SVG file;
* SVG syntax passed in through the command-line;
* a polygon with coordinates passed in through the command-line.
