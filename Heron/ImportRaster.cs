﻿using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Linq;
using System.Data;
using System.Drawing;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Collections;
using GH_IO;
using GH_IO.Serialization;
using OSGeo.GDAL;
using OSGeo.OSR;
using OSGeo.OGR;



namespace Heron
{
    public class ImportRaster : HeronRasterPreviewComponent
    {
        //Class Constructor
        public ImportRaster() : base("Import Raster", "ImportRaster", "Import georeferenced raster data.", "GIS Tools")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for raster data", GH_ParamAccess.item);
            pManager.AddTextParameter("Raster Location", "rasterLocation", "File path for the raster data.", GH_ParamAccess.item);
            pManager.AddTextParameter("Clipped Location", "clippedLocation", "Output folder path for the clipped raster data and preview image.", GH_ParamAccess.item, Path.GetTempPath());
            pManager[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("rasterInfo", "rasterInfo", "List of information about the source dataset.", GH_ParamAccess.item);
            pManager.AddRectangleParameter("rasterExtent", "rasterExtent", "Bounding box for the raster data.", GH_ParamAccess.item);
            pManager.AddTextParameter("clippedRaster", "clippedRaster", "File path for the raster data clipped to the boundary.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Curve boundary = null;
            DA.GetData<Curve>(0, ref boundary);

            string sourceFileLocation = string.Empty;
            DA.GetData<string>(1, ref sourceFileLocation);

            string clippedLocation = string.Empty;
            DA.GetData<string>(2, ref clippedLocation);

            RESTful.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();
            ///Specific settings for getting WMS images
            OSGeo.GDAL.Gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "YES");
            OSGeo.GDAL.Gdal.SetConfigOption("GDAL_SKIP", "WMS");

            ///Read in the raster data
            Dataset datasource = Gdal.Open(sourceFileLocation, Access.GA_ReadOnly);
            OSGeo.GDAL.Driver drv = datasource.GetDriver();

            string srcInfo = string.Empty;

            if (datasource == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The raster datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                return;
            }


            ///Get the spatial reference of the input raster file and set to WGS84 if not known
            ///Set up transform from source to WGS84
            OSGeo.OSR.SpatialReference sr = new SpatialReference(Osr.SRS_WKT_WGS84);
            if (datasource.GetProjection() == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Coordinate Reference System (CRS) is missing.  CRS set automatically set to WGS84.");
            }

            else
            {
                sr = new SpatialReference(datasource.GetProjection());
                if (sr.Validate() != 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Coordinate Reference System (CRS) is unknown or unsupported.  CRS set automatically set to WGS84.");
                    sr.SetWellKnownGeogCS("WGS84");
                }

                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Data source SRS: EPSG:" + sr.GetAttrValue("AUTHORITY", 1));
                }
            }

            srcInfo = Gdal.GDALInfo(datasource, null);

            //OSGeo.OSR.SpatialReference sr = new SpatialReference(ds.GetProjection());
            OSGeo.OSR.SpatialReference dst = new OSGeo.OSR.SpatialReference("");
            dst.SetWellKnownGeogCS("WGS84");
            OSGeo.OSR.CoordinateTransformation coordTransform = new OSGeo.OSR.CoordinateTransformation(sr, dst);
            OSGeo.OSR.CoordinateTransformation revTransform = new OSGeo.OSR.CoordinateTransformation(dst, sr);

            double[] adfGeoTransform = new double[6];
            double[] invTransform = new double[6];
            datasource.GetGeoTransform(adfGeoTransform);
            Gdal.InvGeoTransform(adfGeoTransform, invTransform);
            Band band = datasource.GetRasterBand(1);

            int width = datasource.RasterXSize;
            int height = datasource.RasterYSize;

            ///Dataset bounding box
            double oX = adfGeoTransform[0] + adfGeoTransform[1] * 0 + adfGeoTransform[2] * 0;
            double oY = adfGeoTransform[3] + adfGeoTransform[4] * 0 + adfGeoTransform[5] * 0;
            double eX = adfGeoTransform[0] + adfGeoTransform[1] * width + adfGeoTransform[2] * height;
            double eY = adfGeoTransform[3] + adfGeoTransform[4] * width + adfGeoTransform[5] * height;

            ///Transform to WGS84. 
            ///TODO: Allow for UserSRS
            double[] extMinPT = new double[3] { oX, eY, 0 };
            double[] extMaxPT = new double[3] { eX, oY, 0 };
            coordTransform.TransformPoint(extMinPT);
            coordTransform.TransformPoint(extMaxPT);
            Point3d dsMin = new Point3d(extMinPT[0], extMinPT[1], extMinPT[2]);
            Point3d dsMax = new Point3d(extMaxPT[0], extMaxPT[1], extMaxPT[2]);

            ///Get bounding box for entire raster data
            Rectangle3d datasourceBBox = new Rectangle3d(Plane.WorldXY, Heron.Convert.WGSToXYZ(dsMin), Heron.Convert.WGSToXYZ(dsMax));


            ///https://gis.stackexchange.com/questions/312440/gdal-translate-bilinear-interpolation
            ///set output to georeferenced tiff as a catch-all
            string clippedRasterFile = clippedLocation + Path.GetFileNameWithoutExtension(sourceFileLocation) + "_clipped.tif";
            string previewPNG = clippedLocation + Path.GetFileNameWithoutExtension(sourceFileLocation) + "_preview.png";

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Original Resolution: " + datasource.RasterXSize.ToString() + "x" + datasource.RasterYSize.ToString());

            if (boundary!=null)
            {

                Point3d clipperMin = Heron.Convert.XYZToWGS(boundary.GetBoundingBox(true).Corner(true, false, true));
                Point3d clipperMax = Heron.Convert.XYZToWGS(boundary.GetBoundingBox(true).Corner(false, true, true));

                double lonWest = clipperMin.X;
                double lonEast = clipperMax.X;
                double latNorth = clipperMin.Y;
                double latSouth = clipperMax.Y;

                ///GDALTranslate should also be its own component with full control over options
                var translateOptions = new[]
                {
                    "-of", "GTiff",
                    //"-a_nodata", "0",
                    "-projwin_srs", "WGS84",
                    "-projwin", $"{lonWest}", $"{latNorth}", $"{lonEast}", $"{latSouth}"
                };

                using (Dataset clippedDataset = Gdal.wrapper_GDALTranslate(clippedRasterFile, datasource, new GDALTranslateOptions(translateOptions), null, null))
                {
                    Dataset previewDataset = Gdal.wrapper_GDALTranslate(previewPNG, clippedDataset, null, null, null);

                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Clipped Resolution: " + clippedDataset.RasterXSize.ToString() + "x" + datasource.RasterYSize.ToString());

                    ///clean up
                    clippedDataset.FlushCache();
                    clippedDataset.Dispose();
                    previewDataset.FlushCache();
                    previewDataset.Dispose();

                    AddPreviewItem(previewPNG, BBoxToRect(boundary.GetBoundingBox(true)));
                }

            }

            else
            {
                Dataset previewDataset = Gdal.wrapper_GDALTranslate(previewPNG, datasource, null, null, null);

                ///clean up
                previewDataset.FlushCache();
                previewDataset.Dispose();

                AddPreviewItem(previewPNG, datasourceBBox);
            }

            ///clean up
            datasource.FlushCache();
            datasource.Dispose();

            DA.SetData(0, srcInfo);
            DA.SetData(1, datasourceBBox);
            DA.SetData(2, clippedRasterFile);

        }



        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.raster;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{6834C93A-5FC3-40AE-A7C3-153E96232990}"); }
        }
    }
}
