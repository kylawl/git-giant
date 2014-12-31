GitBifrost
==========

Built for game developers who are used to the world of versioned game data, GitBifrost bridges the worlds of git and large binaries.

## Goals ##
1. Allow users to version large binaries easily with along side source code.
2. Allow users on very large, multi-terabyte projects to fetch binaries exclusivly from a central store. No large file history lives on the client, only the current working set.
3. Binary handling must be transparent to non-power users and other tools so that anyone and anything that uses git doesn't require knowlege of GitBifrost.
4. Allow git power users to work locally in all their normal distributed git glory.

## Getting Started ##


### New Repository ###
#### Initing git-bifrost ####

Bifrost uses git hooks & filters to transparently keep portions of your repo in an external store. In order to install those hooks you need to run `git bifrost init` in a repository.

    git init new/repo/dir
    cd new/repo/dir
    git bifrost init

####Filtering files with .gitattributes####
Add any files and extentions you'd like bifrost to track to your `.gitattributes`

    *.max filter=bifrost
    *.psd filter=bifrost
    *.wav filter=bifrost

####Configuring .gitbifrost####
Bifrost has it's own config file which you use to tell it which stores will be updated when a push is made to a particular remote. It also provides some rules for helping catch new files that may have been missed in `.gitattributes`.

    [repo]
	    text-size-threshold = 5242880
	    bin-size-threshold = 102400

    [store "project-store.onsite"]
      remote = /local/project.git
	    url = /local/project_store

    [store "my-git-store"]
	    remote = https://github.com/user/project.git
	    url = ftps://mydomain.com/project_store
        username = user
        password = pass
        primary = true

In the `[repo]` section, the `text-size-threshold` & `bin-size-threshold` limits are used to help catch new files that may be too big for a git repo. When you perform a commit, Bifrost will verify that the staged files are all within appropriate limits. If they're not, Bifrost will abort the commit. You can now take action by either bumping up your limits or adding the file/filetype to `.gitattributes`.

The `[store]` sections are used to map git remote repositories to bifrost store locations. The idea here being that when pefrorm `git-push` to a particular remote, the stores that are mapped to that remote are also updated automatically.

Each `[store]` section should have a unique name `[store "store-name"]` which adheres to the git-config format.

Currently stores can be accessed from `file://`, `ftp://`, `ftps://`

##### Configuring .gitbifrostuser #####
User specific override file which should be included in `.gitignore`. This file is intended to specify usernames and passwords for git-bifrost stores.

For example `.gitbifrost` may contain `[store "remote_ftp"]` with a username and password for read-only access. With `.gitbifrostuser`, users may override the username and password with their own credentials which enable read/write.

    [store "my-git-store"]
        username = write_access_user
        password = pass

### Cloning Repository ###
Bifrost needs to install some hooks and filters before you do your first checkout so `git bifrost clone` will clone the repo with --no-checkout, install the hooks and then do the checkout.

    git bifrost clone <repository> <directory>

### Common .gitattributes ###

	# Common
	*.bmp  filter=bifrost
	*.exe  filter=bifrost
	*.dae  filter=bifrost
	*.dll  filter=bifrost
	*.fbx  filter=bifrost
	*.ico  filter=bifrost
	*.jpg  filter=bifrost
	*.ma   filter=bifrost
	*.max  filter=bifrost
	*.mb   filter=bifrost
	*.mp3  filter=bifrost
	*.obj  filter=bifrost
	*.ogg  filter=bifrost
	*.png  filter=bifrost
	*.psd  filter=bifrost
	*.so   filter=bifrost
	*.tga  filter=bifrost
	*.ttf  filter=bifrost
	*.tiff filter=bifrost
	*.ztl  filter=bifrost
	*.wav  filter=bifrost

	# UE4
	*.uasset filter=bifrost
	*.umap   filter=bifrost

	# Unity
	*.unity filter=bifrost"
    
### Known Issues ###

### TODO ###
- Sftp support
- Trim internal store after a pushing to a primary store
- If you download a git archive from github, you'll only have the proxy files instead of the actual binary data. Add support for sucking down data from proxy files only without a git repo.
- If you do a normal `git clone` rather than a `git bifrost clone`, you're hooped and you'll have to start over. This kind of sucks
- Add support for compressing data before uploading to store
