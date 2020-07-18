def parse_srs_header(header):
    """
    Parse a header coming from GCP or coordinate file
    :param header (str) line
    :return Proj object
    """
    log.ODM_INFO('Parsing SRS header: %s' % header)
    header = header.strip()
    ref = header.split(' ')
    try:
        if ref[0] == 'WGS84' and ref[1] == 'UTM':
            datum = ref[0]
            utm_pole = (ref[2][len(ref[2]) - 1]).upper()
            utm_zone = int(ref[2][:len(ref[2]) - 1])
            
            proj_args = {
                'zone': utm_zone, 
                'datum': datum
            }

            proj4 = '+proj=utm +zone={zone} +datum={datum} +units=m +no_defs=True'
            if utm_pole == 'S':
                proj4 += ' +south=True'

            srs = CRS.from_proj4(proj4.format(**proj_args))
        elif '+proj' in header:
            srs = CRS.from_proj4(header.strip('\''))
        elif header.lower().startswith("epsg:"):
            srs = CRS.from_epsg(header.lower()[5:])
        else:
            raise RuntimeError('Could not parse coordinates. Bad SRS supplied: %s' % header)
    except RuntimeError as e:
        log.ODM_ERROR('Uh oh! There seems to be a problem with your coordinates/GCP file.\n\n'
                            'The line: %s\n\n'
                            'Is not valid. Projections that are valid include:\n'
                            ' - EPSG:*****\n'
                            ' - WGS84 UTM **(N|S)\n'
                            ' - Any valid proj4 string (for example, +proj=utm +zone=32 +north +ellps=WGS84 +datum=WGS84 +units=m +no_defs)\n\n'
                            'Modify your input and try again.' % header)
        raise RuntimeError(e)
    
    return srs