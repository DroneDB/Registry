<?xml version="1.0" encoding="utf-8" ?>
<ogr:FeatureCollection
     gml:id="aFeatureCollection"
     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
     xmlns:ogr="http://ogr.maptools.org/"
     xmlns:gml="http://www.opengis.net/gml/3.2">
  <!-- no boundedBy on the collection -->
  <ogr:featureMember>
    <ogr:my_layer gml:id="my_layer.12" fid="12" xsi:type="ogr:my_layer_Type">
      <ogr:geom><gml:Point srsName="urn:ogc:def:crs:EPSG::4326" gml:id="my_layer.geom.12"><gml:pos>45.5 9.25</gml:pos></gml:Point></ogr:geom>
      <!-- a comment between properties -->
      <ogr:name>Caf&#233; &amp; &lt;bar&gt;</ogr:name>
      <ogr:empty></ogr:empty>
      <ogr:blank>   </ogr:blank>
      <ogr:nil xsi:nil="true"/>
      <ogr:ATTR-1>0</ogr:ATTR-1>
      <ogr:complex><ogr:a>1</ogr:a><ogr:b/></ogr:complex>
      <ogr:mixed>text <ogr:c>2</ogr:c> tail</ogr:mixed>
      <ogr:cdata><![CDATA[x < y]]></ogr:cdata>
    </ogr:my_layer>
  </ogr:featureMember>
  <ogr:featureMember>
  </ogr:featureMember>
  <ogr:featureMember>
    <ogr:my_layer gml:id="nodot">
      <ogr:name>second</ogr:name>
    </ogr:my_layer>
  </ogr:featureMember>
</ogr:FeatureCollection>
